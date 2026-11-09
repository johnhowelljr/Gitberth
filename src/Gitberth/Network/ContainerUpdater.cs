using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Services;

namespace Gitberth.Network;

/// <param name="Ok">Docker made the change.</param>
/// <param name="Message">What happened, or why not, in the owner's words.</param>
/// <param name="Warnings">Docker's warnings about the change, as it wrote them.</param>
public sealed record LiveResult(bool Ok, string Message, IReadOnlyList<string> Warnings);

/// <summary>
/// docker update for the live fields (sections 2.1 and 2.3): the restart policy, memory, CPUs and the PID limit change
/// while the container runs (or doesn't), with no restart and nothing to recover, so no journal row. Only what can be
/// done live is: Docker reads a 0 as "unchanged", so taking a limit away, or switching a quota to CPUs, is a recreate.
/// </summary>
public sealed class ContainerUpdater(IContainerEngine engine, ContainerLocks locks, Lazy<PortChanger> changer, ActivityLog activity, ILogger<ContainerUpdater> logger)
{
    /// <summary>The change, under the container's lock; refused while a change to it is under way, or when it's a copy a change left behind.</summary>
    public async Task<LiveResult> ApplyAsync(string container, ContainerSettings target, IReadOnlySet<SettingField> fields, CancellationToken ct = default)
    {
        using var held = await locks.TryAcquireAsync(container);
        if (held is null) return Refused($"Gitberth is already changing {container}. Try again when that's done.");
        string? why;
        try
        {
            why = await changer.Value.WhyNotChangeAsync(container, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Docker or the journal didn't say whether a change holds it: nothing is changed on a guess.
            return Refused($"Couldn't check {container} before changing it ({ex.Message}).");
        }
        if (why is not null) return Refused(why);
        return await ApplyToAsync(container, target, fields, ct);
    }

    /// <summary>The same, for a caller that holds the container's lock (a Git app's AppLocks, or the change engine).</summary>
    public async Task<LiveResult> ApplyToAsync(string container, ContainerSettings target, IReadOnlySet<SettingField> fields, CancellationToken ct = default)
    {
        var live = fields.Where(SettingFields.Live.Contains).ToHashSet();
        if (live.Count == 0) return new LiveResult(true, "Nothing to change live.", []);
        JsonObject? inspect;
        try
        {
            inspect = await engine.InspectContainerAsync(container, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Refused($"Couldn't read {container} from Docker ({ex.Message}).");
        }
        if (inspect is null) return Refused($"There's no container called {container}.");
        var before = ContainerSettingsJson.Read(inspect, null);
        var hostConfig = inspect["HostConfig"] as JsonObject;
        if (!CanApplyLive(before, target, live, hostConfig))
            return Refused($"{container}'s {Words(live)} can't be changed without recreating it: Docker can't take a limit away, or switch a CPU quota to CPUs, live.");
        var body = Body(target, live, hostConfig, before);
        JsonObject answer;
        try
        {
            answer = await engine.UpdateAsync(ContainerPorts.Str(inspect, "Id"), body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "docker update of {Container} failed", container);
            await RecordAsync(container, $"Docker didn't change its {Words(live)} live: {ex.Message}", ActivityTone.Warning);
            return Refused($"Docker didn't change {container}'s {Words(live)}: {ex.Message}");
        }
        var warnings = ContainerSettingsJson.StrList(answer["Warnings"])?.Where(w => w.Length > 0).ToList() ?? [];
        var summary = SettingsDiff.Summary(before, target, live);
        await RecordAsync(container, $"{Capital(summary)}, live: no restart.", ActivityTone.Neutral);
        return new LiveResult(true, $"{Capital(summary)}. No restart." + string.Concat(warnings.Select(w => " Docker: " + w)), warnings);
    }

    // A live change's one record in Activity is this one (the settings service writes none for it); only a record, so a
    // failure to write it changes nothing. The engine's own updater, made without one, keeps no Activity.
    private async Task RecordAsync(string container, string text, ActivityTone tone)
    {
        try
        {
            if (activity is { } log) await log.AddAsync(container, null, ActivityKind.Live, text, tone, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation(ex, "Couldn't write the Activity entry for the live change to {Container}", container);
        }
    }

    /// <summary>
    /// The update body: only the fields' keys, and with <paramref name="live"/> (the settings it has now) only the values
    /// that change, so a CPU-shares change on a container limited by a quota leaves the quota alone. Memory goes with its
    /// swap: twice the new limit unless one is set, and an unlimited swap (-1) stays unlimited, since Docker refuses a limit
    /// above the swap it has. Taking the PID limit away is -1. Pure.
    /// </summary>
    public static JsonObject Body(ContainerSettings target, IReadOnlySet<SettingField> fields, JsonObject? currentHostConfig, ContainerSettings? live = null)
    {
        var body = new JsonObject();
        var r = target.Resources;
        var now = live?.Resources;
        if (fields.Contains(SettingField.RestartPolicy)) body["RestartPolicy"] = ContainerSettingsJson.RestartJson(target.Restart);
        if (fields.Contains(SettingField.Memory))
        {
            if (r.MemoryBytes is { } memory && (now is null || now.MemoryBytes != memory || now.MemorySwapBytes != r.MemorySwapBytes))
            {
                body["Memory"] = memory;
                body["MemorySwap"] = r.MemorySwapBytes ?? (ContainerPorts.Number(currentHostConfig, "MemorySwap") == -1 ? -1 : memory * 2);
            }
            if (r.MemoryReservationBytes is { } reservation && now?.MemoryReservationBytes != reservation) body["MemoryReservation"] = reservation;
        }
        if (fields.Contains(SettingField.Cpu))
        {
            if (r.Cpus is { } cpus && now?.Cpus != cpus) body["NanoCpus"] = (long)Math.Round(cpus * 1e9);
            if (r.CpuShares is { } shares && now?.CpuShares != shares) body["CpuShares"] = shares;
            if (r.CpusetCpus is { } cpuset && now?.CpusetCpus != cpuset) body["CpusetCpus"] = cpuset;
        }
        if (fields.Contains(SettingField.PidsLimit)) body["PidsLimit"] = r.PidsLimit ?? -1;
        return body;
    }

    /// <summary>
    /// Whether docker update can make the change: not when it takes away a memory limit, reservation or swap limit, or a
    /// CPU limit, shares or set (Docker reads 0 and "" as "unchanged"), nor when it leaves the memory limit above its swap
    /// limit (Docker refuses it), nor when it switches a quota-based CPU limit to other CPUs (Docker refuses both at once,
    /// and update can't clear the quota); the shares or set of such a container change live. Pure.
    /// </summary>
    public static bool CanApplyLive(ContainerSettings live, ContainerSettings target, IReadOnlySet<SettingField> fields, JsonObject? currentHostConfig)
    {
        if (fields.Any(f => !SettingFields.Live.Contains(f))) return false;
        var (now, then) = (live.Resources, target.Resources);
        if (fields.Contains(SettingField.Memory))
        {
            if (now.MemoryBytes is not null && then.MemoryBytes is null) return false;
            if (now.MemoryReservationBytes is not null && then.MemoryReservationBytes is null) return false;
            if (now.MemorySwapBytes is not null && then.MemorySwapBytes is null && then.MemoryBytes is null) return false;
            if (then.MemorySwapBytes is not null && then.MemoryBytes is null) return false;
            if (then.MemoryBytes is { } memory && then.MemorySwapBytes is { } swap && swap != -1 && swap < memory) return false;
        }
        if (fields.Contains(SettingField.Cpu))
        {
            if (now.Cpus is not null && then.Cpus is null) return false;
            if (now.CpuShares is not null && then.CpuShares is null) return false;
            if (now.CpusetCpus is not null && then.CpusetCpus is null) return false;
            if (then.Cpus is not null && then.Cpus != now.Cpus && ContainerPorts.Number(currentHostConfig, "CpuQuota") > 0) return false;
        }
        return true;
    }

    /// <summary>
    /// The swap limit that goes with a new memory limit when the owner didn't change the swap: one set explicitly keeps its
    /// ratio to the memory limit (512 MiB with 768 MiB of swap raised to 1 GiB gets 1.5 GiB), an unlimited one (-1) stays
    /// unlimited, and none stays none (Docker's twice the limit). Without a memory limit, an explicit swap limit goes too.
    /// Docker refuses a memory limit above the swap limit, live or on a create. Pure.
    /// </summary>
    public static ContainerSettings FollowSwap(ContainerSettings loaded, ContainerSettings wanted)
    {
        var (was, now) = (loaded.Resources, wanted.Resources);
        if (now.MemorySwapBytes != was.MemorySwapBytes || now.MemoryBytes == was.MemoryBytes || was.MemorySwapBytes is not { } swap || swap == -1) return wanted;
        long? follows = now.MemoryBytes is not { } memory ? null
            : was.MemoryBytes is { } before && before > 0 ? (long)Math.Round(swap / (double)before * memory)
            : null;
        if (follows is { } s && now.MemoryBytes is { } limit && s < limit) follows = limit;
        return wanted with { Resources = now with { MemorySwapBytes = follows } };
    }

    private static LiveResult Refused(string why) => new(false, why, []);

    // "memory limit and restart policy"
    private static string Words(IReadOnlySet<SettingField> fields) => RecreateRules.Join(fields.Order().Select(f => f switch
    {
        SettingField.Memory => "memory limit",
        SettingField.Cpu => "CPU limit",
        SettingField.PidsLimit => "process limit",
        SettingField.RestartPolicy => "restart policy",
        _ => f.ToString(),
    }).ToList());

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
