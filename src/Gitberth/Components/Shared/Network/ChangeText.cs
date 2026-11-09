using System.Globalization;
using Gitberth.Apps;
using Gitberth.Network;
using MudBlazor;

namespace Gitberth.Components.Shared.Network;

/// <summary>What kind of change the dialog shows, which sets its words, its icon and its button.</summary>
public enum ChangeForm
{
    /// <summary>The container is recreated with the new settings (or ports).</summary>
    Recreate,
    /// <summary>docker update: no restart.</summary>
    Live,
    /// <summary>A newer image, pulled: a recreate on it.</summary>
    Update,
    /// <summary>Back to the image it ran before.</summary>
    Rollback,
    /// <summary>Docker Compose owns the container: the edit for its file.</summary>
    Compose,
    /// <summary>Nothing in Docker changes now: Gitberth's own data, a Git app's settings, or a missing container's snapshot.</summary>
    Save,
}

/// <summary>The change dialog's words and the way it lays out a plan's lines (apply-dialog.html). Pure, so it's tested.</summary>
public static class ChangeText
{
    /// <summary>Which form the dialog takes for a plan.</summary>
    public static ChangeForm FormOf(SettingsPlan plan) =>
        plan.ComposeSnippet is not null ? ChangeForm.Compose
        : plan.Change is not { } change ? ChangeForm.Save
        : change.Kind == ChangeKind.Update ? ChangeForm.Update
        : change.Kind == ChangeKind.Rollback ? ChangeForm.Rollback
        : change.LiveOnly ? ChangeForm.Live
        : ChangeForm.Recreate;

    /// <summary>How many changes the plan makes: its CHANGE lines, ports and settings together.</summary>
    public static int Count(SettingsPlan plan) =>
        Math.Max(1, (plan.Change?.Changes.Count ?? 0) + plan.Lines.Count(l => l.Field != SettingField.Ports || plan.Change is not { Changes.Count: > 0 }));

    public static string Title(ChangeForm form, string name, int count) => form switch
    {
        ChangeForm.Update => $"Update {name}?",
        ChangeForm.Rollback => $"Roll back {name}?",
        ChangeForm.Compose => $"Change {name} in its Compose file",
        ChangeForm.Live => count == 1 ? $"Apply 1 change to {name}?" : $"Apply {Num(count)} changes to {name}?",
        ChangeForm.Save => count == 1 ? $"Save 1 change to {name}?" : $"Save {Num(count)} changes to {name}?",
        _ => count == 1 ? $"Apply 1 change to {name}?" : $"Apply {Num(count)} changes to {name}?",
    };

    public static string Sub(ChangeForm form, string name, bool running) => form switch
    {
        ChangeForm.Update => $"Gitberth replaces the container with one on the new image. Its data stays in its volumes, and the old image is kept for Roll back.",
        ChangeForm.Rollback => $"Gitberth replaces the container with one on the image it ran before. Its data stays in its volumes.",
        ChangeForm.Compose => $"Docker Compose manages {name} and would undo a change made here, so this is the change to make in its file.",
        ChangeForm.Live => $"Docker changes it in place: {name} keeps running. No restart.",
        ChangeForm.Save => "Nothing in Docker changes now.",
        _ => running
            ? $"Gitberth replaces the container with a new one that has these settings. Its data stays in its volumes."
            : $"{name} is stopped: Gitberth recreates it stopped, with these settings. Its data stays in its volumes.",
    };

    /// <summary>The primary button.</summary>
    public static string Button(ChangeForm form, string name) => form switch
    {
        ChangeForm.Update => $"Update {name}",
        ChangeForm.Rollback => $"Roll back {name}",
        ChangeForm.Live => "Apply, no restart",
        ChangeForm.Save => "Save",
        _ => $"Recreate {name}",
    };

    /// <summary>The dialog's title while it runs.</summary>
    public static string Running(ChangeForm form, string name) => form switch
    {
        ChangeForm.Update => $"Updating {name}",
        ChangeForm.Rollback => $"Rolling back {name}",
        ChangeForm.Live => $"Changing {name}",
        _ => $"Recreating {name}",
    };

    /// <summary>
    /// Why a started change can't be followed: the job that came back runs another plan (a change someone else started
    /// on the same container), so this one was never made and must never be shown as done. Null when the job is this
    /// plan's own, or when there was no plan to compare (a job handed to the dialog to follow).
    /// </summary>
    public static string? NotThisChange(PortChangeJob job, PortChangePlan? plan, string name) =>
        plan is null || ReferenceEquals(job.Plan, plan) ? null
        : $"Gitberth is already changing {name}. Try again when that's done.";

    public static string Icon(ChangeForm form) => form switch
    {
        ChangeForm.Update => Icons.Material.Outlined.CloudDownload,
        ChangeForm.Rollback => Icons.Material.Outlined.Undo,
        ChangeForm.Compose => Icons.Material.Outlined.AccountTree,
        ChangeForm.Live => Icons.Material.Outlined.Tune,
        ChangeForm.Save => Icons.Material.Outlined.Save,
        _ => Icons.Material.Outlined.Sync,
    };

    /// <summary>
    /// The KEEPS line as chips: "Its name, image postgres:18 (3a42…, not pulled again), command and hostname." gives Name,
    /// Image postgres:18 (3a42…, not pulled again), Command, Hostname. Commas and "and" inside brackets stay put.
    /// </summary>
    public static IReadOnlyList<string> Keeps(string keeps)
    {
        var text = keeps.Trim().TrimEnd('.').Trim();
        foreach (var lead in new[] { "Its ", "It keeps its ", "Keeps its ", "Keeps " })
            if (text.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
            {
                text = text[lead.Length..];
                break;
            }
        if (text.Length == 0) return [];
        var parts = SplitTop(text, ", ");
        // The last item is joined with " and " (outside brackets).
        if (parts.Count > 0 && LastTopAnd(parts[^1]) is int at)
        {
            var last = parts[^1];
            parts[^1] = last[..at];
            parts.Add(last[(at + 5)..]);
        }
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).Select(Capital).ToList();
    }

    /// <summary>
    /// A KEEPS chip with what it keeps: "Name" becomes ("Name", "postgres"), "Restart policy" ("Restart policy", "Unless
    /// stopped"). Mono says the value is one as typed (a name, a command), not words about it ("the image's", "6
    /// variables"). A chip that already says its value ("Image postgres:18 (3a42…)", "Network bridge"), or one this doesn't
    /// know, comes back with no value. Secrets in the command are masked.
    /// </summary>
    public static (string Label, string? Value, bool Mono) KeepWithValue(string chip, string container, ContainerSettings loaded, IReadOnlyCollection<string> secretKeys) =>
        chip.ToLowerInvariant() switch
        {
            "name" => (chip, container, true),
            "command" => loaded.Command is { Count: > 0 } command ? (chip, string.Join(' ', Security.Masking.Args(command, secretKeys)), true) : (chip, "the image's", false),
            "entrypoint" => loaded.Entrypoint is { Count: > 0 } entry ? (chip, string.Join(' ', Security.Masking.Args(entry, secretKeys)), true) : (chip, "the image's", false),
            "environment" => (chip, loaded.Env.Count == 1 ? "1 variable" : $"{Num(loaded.Env.Count)} variables", false),
            "restart policy" => (chip, loaded.Restart.Name switch { "always" => "Always", "on-failure" => "On failure", "no" => "Never", _ => "Unless stopped" }, false),
            "hostname" => loaded.Hostname is { Length: > 0 } host ? (chip, host, true) : (chip, "Docker's", false),
            "working directory" => loaded.WorkingDir is { } dir ? (chip, dir, true) : (chip, "the image's", false),
            "user" => loaded.User is { } user ? (chip, user, true) : (chip, "the image's", false),
            "labels" => (chip, loaded.Labels.Count == 1 ? "1 label" : $"{Num(loaded.Labels.Count)} labels", false),
            "volumes" => loaded.Mounts.Count == 0 ? (chip, "none", false)
                : (chip, string.Join(", ", loaded.Mounts.Select(m => m.Kind == MountKind.Tmpfs ? "tmpfs" : m.Source).Take(3)), true),
            _ => (chip, null, false),
        };

    /// <summary>
    /// A list setting's change, item by item: what goes and what comes (the label team=ops added, an extra host dropped),
    /// what stays left out. Null for a field whose value isn't a list, or when only the order changed.
    /// </summary>
    public static (IReadOnlyList<string> Gone, IReadOnlyList<string> New)? ListChange(SettingField? field, ContainerSettings before, ContainerSettings after,
        IReadOnlyCollection<string> secretKeys)
    {
        if (Items(field, before, secretKeys) is not { } a || Items(field, after, secretKeys) is not { } b) return null;
        var gone = a.Where(i => !b.Contains(i)).ToList();
        var added = b.Where(i => !a.Contains(i)).ToList();
        return gone.Count + added.Count == 0 ? null : (gone, added);
    }

    // A list setting's items as the change lines write them; null for a field that isn't a list.
    private static List<string>? Items(SettingField? field, ContainerSettings s, IReadOnlyCollection<string> secretKeys) => field switch
    {
        SettingField.Labels => s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={Security.Masking.Value(l.Key, l.Value, secretKeys)}").ToList(),
        SettingField.Sysctls => s.Sysctls.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}").ToList(),
        SettingField.ExtraHosts => [.. s.ExtraHosts],
        SettingField.Dns => [.. s.Dns, .. s.DnsSearch.Select(d => "search " + d), .. s.DnsOptions.Select(o => "option " + o)],
        SettingField.SecurityOpt => [.. s.SecurityOpt],
        SettingField.Capabilities => [.. s.CapAdd.Select(c => "+" + c), .. s.CapDrop.Select(c => "-" + c)],
        SettingField.Devices => s.Devices.Select(d => $"{d.HostPath} → {d.ContainerPath}").ToList(),
        SettingField.Ulimits => s.Ulimits.Select(u => $"{u.Name} {u.Soft.ToString(CultureInfo.InvariantCulture)}:{u.Hard.ToString(CultureInfo.InvariantCulture)}").ToList(),
        _ => null,
    };

    /// <summary>How an IMPACT line is drawn: its icon and tone, by what it says.</summary>
    public static (string Icon, string Tone) ImpactLook(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.StartsWith("if the new", StringComparison.Ordinal) || l.Contains("comes back", StringComparison.Ordinal))
            return (Icons.Material.Outlined.Shield, "good");
        if (l.Contains("lost", StringComparison.Ordinal) || l.Contains("refuses", StringComparison.Ordinal) || l.Contains("doesn't undo", StringComparison.Ordinal)
            || l.Contains("may fail", StringComparison.Ordinal))
            return (Icons.Material.Outlined.WarningAmber, "serious");
        if (l.Contains("domain", StringComparison.Ordinal))
            return (Icons.Material.Outlined.Public, "warn");
        if (l.Contains(" uses ", StringComparison.Ordinal) || l.Contains(" use ", StringComparison.Ordinal) || l.Contains("connections", StringComparison.Ordinal)
            || l.Contains("by name", StringComparison.Ordinal))
            return (Icons.Material.Outlined.AccountTree, "neutral");
        if (l.Contains("offline", StringComparison.Ordinal) || l.Contains("restarts", StringComparison.Ordinal) || l.Contains("stopped", StringComparison.Ordinal))
            return (Icons.Material.Outlined.Timer, "warn");
        return (Icons.Material.Outlined.Info, "info");
    }

    /// <summary>A step's place in the run: "Step 6 of 9 · 1.9 s so far".</summary>
    public static string Progress(PortChangeStep? current, int done, int count, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s so far";
        if (count == 0) return seconds;
        var at = current?.Number ?? Math.Min(done + 1, count);
        return $"Step {Num(at)} of {Num(count)} · {seconds}";
    }

    // Splits at the separator where no bracket is open.
    private static List<string> SplitTop(string text, string separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(text[start..i]);
                start = i + separator.Length;
                i += separator.Length - 1;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    // Where the last " and " outside brackets is; null when there's none.
    private static int? LastTopAnd(string text)
    {
        int? found = null;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && string.CompareOrdinal(text, i, " and ", 0, 5) == 0) found = i;
        }
        return found;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
