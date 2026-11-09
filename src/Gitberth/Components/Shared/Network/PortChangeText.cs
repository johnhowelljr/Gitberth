using System.Globalization;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Components.Shared.Network;

/// <summary>The ways into a standalone container's port change (5.4): one dialog, PortChangeDialog, for all of them.</summary>
public enum PortChangeKind
{
    /// <summary>[Publish on LAN] on the Addresses card: one port onto the LAN, with a host port to pick.</summary>
    Publish,
    /// <summary>[Apply…] on the Ports card: the draft as it stands.</summary>
    Apply,
    /// <summary>[…] › Change host port… on a LAN address.</summary>
    ChangeHostPort,
    /// <summary>[…] › Unpublish… on a LAN address.</summary>
    Unpublish,
}

/// <summary>The port change dialog's words (5.4, 5.5): its title and button for each way in, its progress, and "Last change".</summary>
public static class PortChangeText
{
    /// <param name="port">The container port the change is about, for Unpublish's title.</param>
    public static string Title(PortChangeKind kind, string name, int port = 0) => kind switch
    {
        PortChangeKind.Publish => $"Publish {name} on the LAN",
        PortChangeKind.Apply => $"Recreate {name}?",
        PortChangeKind.ChangeHostPort => $"Change {name}'s host port",
        _ => $"Unpublish {port.ToString(CultureInfo.InvariantCulture)} on {name}?",
    };

    /// <summary>The button that starts the change.</summary>
    public static string Button(PortChangeKind kind) => kind switch
    {
        PortChangeKind.Publish => "Recreate and publish",
        PortChangeKind.Unpublish => "Recreate and unpublish",
        _ => "Recreate",
    };

    /// <summary>The dialog's title once the change runs.</summary>
    public static string Recreating(string name) => $"Recreating {name}";

    /// <summary>Under the steps while the change runs.</summary>
    public const string CarriesOn = "You can close this: it carries on, and the Ports card shows how it went.";

    /// <summary>Where a change is, in one line: "Recreating postgres: step 4 of 9…"; between steps, "Recreating postgres…".</summary>
    public static string Progress(string name, PortChangeStep? current) =>
        current is { } step
            ? $"Recreating {name}: step {step.Number.ToString(CultureInfo.InvariantCulture)} of {step.Count.ToString(CultureInfo.InvariantCulture)}…"
            : $"Recreating {name}…";

    /// <summary>
    /// The container's last port change, from the journal: "Last change: 4 Nov 21:14 · published 5432/tcp on the LAN ·
    /// offline 2.1 s." A change that didn't finish says where it stands instead of the time offline.
    /// </summary>
    /// <param name="zone">The time zone the time is shown in: the server's.</param>
    public static string LastChange(ContainerChange? row, TimeZoneInfo zone)
    {
        if (row is null) return "Last change: none.";
        var when = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.StartedAt, DateTimeKind.Utc), zone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
        var how = row.Outcome switch
        {
            ChangeOutcome.Done => row.OfflineMs is int ms ? $" · offline {(ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} s" : "",
            ChangeOutcome.RolledBack => " · rolled back",
            ChangeOutcome.NothingChanged => " · nothing changed",
            ChangeOutcome.NeedsDecision => " · waits for your decision",
            _ when row.Step == ChangeStep.WaitingForStart => " · waits for its first start",
            _ => " · under way",
        };
        return $"Last change: {when} · {row.Summary}{how}.";
    }
}

/// <summary>
/// The ports each way into a change asks the recreate for: every port the container should have afterwards, starting from
/// the ones it has (<see cref="ContainerPorts.Rules"/>), with only the one being changed different.
/// </summary>
public static class PortChanges
{
    /// <summary>Publish on LAN: the port goes on the LAN at the host port, whatever it had; the others stay as they are.</summary>
    public static List<PortRule> Publish(IReadOnlyList<PortRule> current, int port, string protocol, int hostPort)
    {
        var key = ContainerPorts.Key(port, protocol);
        return [.. current.Where(r => r.Key != key), new PortRule(port, protocol, PortAccess.Lan, hostPort)];
    }

    /// <summary>Change host port: the binding moves to the host port, on the address it had.</summary>
    public static List<PortRule> ChangeHostPort(IReadOnlyList<PortRule> current, PortRule binding, int hostPort) =>
        current.Select(r => r == binding ? r with { HostPort = hostPort } : r).ToList();

    /// <summary>Unpublish: the binding goes; a port left with none stays, Internal, so containers still reach it by name.</summary>
    public static List<PortRule> Unpublish(IReadOnlyList<PortRule> current, PortRule binding)
    {
        var rest = current.Where(r => r != binding).ToList();
        if (rest.All(r => r.Key != binding.Key)) rest.Add(new PortRule(binding.ContainerPort, binding.Protocol, PortAccess.Internal));
        return rest;
    }

    /// <summary>
    /// The ports [Publish on LAN] can publish: each that isn't on the LAN yet (Internal, or on the host only). The
    /// database's own port first, else the lowest TCP one (5.1), then the other TCP ones, then UDP.
    /// </summary>
    public static List<PortRule> Candidates(IReadOnlyList<PortRule> current, int? databasePort) =>
        current.GroupBy(r => r.Key, StringComparer.Ordinal)
            .Where(g => g.All(r => r.Access is PortAccess.Internal or PortAccess.HostOnly))
            .Select(g => g.First())
            .OrderBy(r => r.Protocol != "tcp" ? 2 : r.ContainerPort == databasePort ? 0 : 1)
            .ThenBy(r => r.ContainerPort)
            .ToList();
}

/// <summary>
/// The banner on a container's page about its last port change (5.5): what a restart of Gitberth in the middle of it led
/// to, until it's dismissed, or a change whose two containers are both kept until the owner chooses.
/// </summary>
/// <param name="Warning">Anything but a change that finished.</param>
/// <param name="Decide">Both containers are kept: [Keep the new one] or [Put the original back]. It's never just dismissed.</param>
public sealed record ChangeBanner(string Text, bool Warning, bool Decide)
{
    public static ChangeBanner? Of(ContainerChange? row) => row switch
    {
        null => null,
        { Outcome: ChangeOutcome.NeedsDecision } => new(
            row.Notice is { Length: > 0 } notice ? notice : row.Error is { Length: > 0 } error ? error : $"A port change on {row.ContainerName} waits for your decision.",
            Warning: true, Decide: true),
        { Notice: { Length: > 0 } notice } => new(notice, Warning: row.Outcome != ChangeOutcome.Done, Decide: false),
        _ => null,
    };
}

/// <summary>
/// The lines a Compose-managed container's Ports card gives for its change (5.6): where its file is, and the command that
/// applies it. A stack Portainer keeps has its file inside Portainer's own container (under /data/compose), where neither
/// the path nor a command on the host reaches it: its stack editor is the place instead.
/// </summary>
public static class ComposeText
{
    /// <summary>"In /opt/db/docker-compose.yml:", "In its Compose file:" when its labels don't say, or "In its stack in Portainer:".</summary>
    public static string FileLine(ComposeService compose) => InPortainer(compose) ? "In its stack in Portainer:" : $"In {compose.File}:";

    /// <summary>
    /// What to run on the host after changing the file: cd /opt/db &amp;&amp; docker compose -p db -f
    /// /opt/db/docker-compose.yml up -d postgres. The project and files come from Compose's labels, so it picks the
    /// project the stack was started as from any folder. Null when the labels don't name the service, or Portainer keeps it.
    /// </summary>
    public static string? CommandLine(ComposeService compose) =>
        InPortainer(compose) || compose.Command is not { } command ? null
        : compose.WorkingDir is { } dir ? $"cd {ConnectionDetails.Quoted(dir)} && {command}"
        : command;

    /// <summary>Portainer keeps the stack: its file is under /data/compose, in Portainer's own container.</summary>
    public static bool InPortainer(ComposeService compose) =>
        compose.ConfigFiles.Count > 0 && compose.ConfigFiles.All(f => f.StartsWith("/data/compose/", StringComparison.Ordinal));
}

/// <summary>An old Gitberth port forwarder left on the host, and the address it publishes the container's port on, for the alert on its Ports card.</summary>
/// <param name="Publishes">10.1.1.80:5432; the container port (5432/tcp) when Docker didn't say.</param>
public sealed record ForwarderNotice(string Name, string Publishes);

/// <summary>
/// The port forwarders an older Gitberth started next to a container (socat, named gb-publish-&lt;container&gt;-&lt;port&gt;):
/// none are made any more, but one left on the host still publishes the container's port, so its Ports card says so.
/// </summary>
public static class LegacyForwarders
{
    /// <summary>The forwarders publishing a port of the container, by the label each carries ("postgres:5432"), with that port.</summary>
    public static List<(string Name, int Port)> Of(string container, IEnumerable<ContainerRow> rows) =>
        rows.Select(r => (r.Container.Name, Target: Target(r.Container.Labels.GetValueOrDefault(ContainerService.PublishesLabel))))
            .Where(r => r.Target?.Container == container)
            .Select(r => (r.Name, r.Target!.Value.Port))
            .ToList();

    private static (string Container, int Port)? Target(string? label) =>
        label is not null && label.LastIndexOf(':') is > 0 and var colon
        && int.TryParse(label[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? (label[..colon], port)
            : null;
}
