using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Security;

namespace Gitberth.Apps;

/// <summary>
/// What an applied settings change says in the audit log: one entry with every field that changed (before and after, as
/// the change's lines show them, secrets masked), the name on its own (a rename), and one entry per variable added,
/// changed or removed: its name, scope and whether it's secret, and its value only when it isn't (cut short).
/// </summary>
internal static class SettingsAudit
{
    /// <summary>The most of a plain variable's value the log keeps.</summary>
    internal const int ValueMost = 100;

    /// <summary>The entries for an applied plan; <paramref name="error"/> when it didn't work.</summary>
    public static List<AuditEntry> For(AppSummary app, int? appId, SettingsPlan plan, string? error = null)
    {
        var entries = new List<AuditEntry>();
        var outcome = error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed;
        var lines = plan.Lines.Where(l => l.Field != SettingField.Env).ToList();
        // Variables added or removed with their secret mark say so in their own entries: the list of secret names would
        // say it twice. A mark changed on a variable that stays is said here.
        var before = plan.Edit.Loaded.Env.Select(e => e.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        var after = plan.Edit.Wanted.Env.Select(e => e.Key.Trim()).ToHashSet(StringComparer.Ordinal);
        var marked = plan.Edit.LoadedOptions.SecretKeys.ToHashSet(StringComparer.Ordinal);
        marked.SymmetricExceptWith(plan.Edit.WantedOptions.SecretKeys);
        if (marked.All(k => before.Contains(k) != after.Contains(k)))
            lines.RemoveAll(l => l.Field is null && l.Label == "Secret variables");
        var name = lines.FirstOrDefault(l => l.Field is null && l.Label == "Name");
        var title = name?.Before ?? app.Title;
        if (name is not null)
        {
            lines.Remove(name);
            if (error is null)
                entries.Add(Entry(app, appId, "app.renamed", $"Renamed {name.Before} to {name.After}.", [new AuditChange("Name", name.Before, name.After)], outcome));
        }
        if (lines.Count > 0)
        {
            // The Network tab's ports (and the web marker with them) are a ports change; anything else is a settings change.
            var ports = lines.All(l => l.Field == SettingField.Ports || (l.Field is null && l.Label == "Web port"));
            var names = lines.Select(l => ports && l.Field == SettingField.Ports ? "ports" : Lower(l.Label)).Distinct().ToList();
            var what = names.Count <= 3 ? AuditText.Join(names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
            entries.Add(Entry(app, appId, ports ? "app.ports.changed" : "app.settings.changed",
                error is null ? (ports ? $"Changed {title}'s ports." : $"Changed {title}'s settings: {what}.")
                    : (ports ? $"Couldn't change {title}'s ports: {Reason(error)}." : $"Couldn't change {title}'s settings ({what}): {Reason(error)}."),
                lines.Select(l => new AuditChange(l.Label, l.Before, l.After)).ToList(), outcome));
        }
        var secrets = plan.Edit.LoadedOptions.SecretKeys.Concat(plan.Edit.WantedOptions.SecretKeys).ToHashSet(StringComparer.Ordinal);
        entries.AddRange(Variables(app, appId, title, plan.Edit.Loaded.Env, plan.Edit.Wanted.Env, secrets, app.Source == AppSource.Git, error));
        return entries;
    }

    /// <summary>One entry per variable added, changed or removed, by its name and scope.</summary>
    public static IEnumerable<AuditEntry> Variables(AppSummary app, int? appId, string title, IReadOnlyList<EnvSetting> before, IReadOnlyList<EnvSetting> after,
        IReadOnlySet<string> secretKeys, bool scoped, string? error = null)
    {
        var a = Index(before);
        var b = Index(after);
        var outcome = error is null ? AuditOutcome.Succeeded : AuditOutcome.Failed;
        foreach (var key in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var was);
            b.TryGetValue(key, out var now);
            var variable = (now ?? was)!;
            var secret = IsSecret(was, secretKeys) || IsSecret(now, secretKeys);
            var noun = secret ? "secret variable" : "variable";
            var scope = scoped ? $" ({Scope(variable.Scope)})" : "";
            var failed = error is null ? "" : $": {Reason(error)}";
            if (was is null && now is not null)
            {
                var changes = new List<AuditChange> { new("Value", null, secret ? null : Value(now.Value)), new("Secret", null, AuditText.OnOff(secret)) };
                if (scoped) changes.Add(new("Scope", null, Scope(now.Scope)));
                if (now.BuildTime) changes.Add(new("Build time", null, "on"));
                yield return Entry(app, appId, "app.env.added", error is null ? $"Added the {noun} {now.Key} to {title}{scope}." : $"Couldn't add the {noun} {now.Key} to {title}{failed}.",
                    changes, outcome, now.Key);
            }
            else if (was is not null && now is null)
            {
                yield return Entry(app, appId, "app.env.removed",
                    error is null ? $"Removed the {noun} {was.Key} from {title}{scope}." : $"Couldn't remove the {noun} {was.Key} from {title}{failed}.",
                    [new AuditChange("Value", secret ? null : Value(was.Value), null), new AuditChange("Secret", AuditText.OnOff(secret), null)], outcome, was.Key);
            }
            else if (was is not null && now is not null)
            {
                var changes = new List<AuditChange>();
                if (was.Value != now.Value) changes.Add(new("Value", secret ? null : Value(was.Value), secret ? null : Value(now.Value)));
                if (was.Secret != now.Secret) changes.Add(new("Secret", AuditText.OnOff(was.Secret), AuditText.OnOff(now.Secret)));
                if (was.BuildTime != now.BuildTime) changes.Add(new("Build time", AuditText.OnOff(was.BuildTime), AuditText.OnOff(now.BuildTime)));
                if (changes.Count == 0) continue;
                yield return Entry(app, appId, "app.env.changed",
                    error is null ? (was.Value != now.Value ? $"Changed the {noun} {now.Key} of {title}{scope}." : $"Changed the {noun} {now.Key} of {title}{scope}: {Marks(changes)}.")
                        : $"Couldn't change the {noun} {now.Key} of {title}{failed}.",
                    changes, outcome, now.Key);
            }
        }
    }

    /// <summary>"3000/tcp on the LAN at 8101 (web), 5432/tcp inside its networks": an app's ports as a change shows them.</summary>
    public static string Ports(IEnumerable<PortMapping> ports)
    {
        var list = ports.OrderBy(p => p.ContainerPort).ThenBy(p => p.Protocol, StringComparer.Ordinal).Select(p =>
        {
            var where = p.Access switch
            {
                Network.PortAccess.Internal => "inside its networks",
                Network.PortAccess.HostOnly => $"on the host only at {p.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a random port"}",
                Network.PortAccess.Custom => $"on {p.HostIp} at {p.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a random port"}",
                _ => $"on the LAN at {p.HostPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a random port"}",
            };
            return $"{p.ContainerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{p.Protocol} {where}{(p.IsWeb ? " (web)" : "")}";
        }).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    // The container's own variables by name and scope; the image's untouched ones aren't the app's to record.
    private static Dictionary<string, EnvSetting> Index(IReadOnlyList<EnvSetting> env)
    {
        var map = new Dictionary<string, EnvSetting>(StringComparer.Ordinal);
        foreach (var e in env.Where(e => e.Key.Trim().Length > 0)) map[EnvScopes.Named(e.Key.Trim(), e.Scope)] = e with { Key = e.Key.Trim() };
        return map;
    }

    // Marked secret, named like one, listed as one, or holding one (a URL's password): its value never goes in the log.
    private static bool IsSecret(EnvSetting? e, IReadOnlySet<string> secretKeys) =>
        e is not null && (e.Secret || secretKeys.Contains(e.Key) || Masking.IsSecret(e.Key, e.Value));

    private static string Value(string value) => value.Length <= ValueMost ? value : value[..(ValueMost - 1)] + "…";

    private static string Scope(EnvScope scope) => scope switch
    {
        EnvScope.Production => "production only",
        EnvScope.Preview => "previews only",
        _ => "all deploys",
    };

    private static string Marks(List<AuditChange> changes) =>
        AuditText.Join(changes.Select(c => c.Field == "Secret" ? (c.To == "on" ? "now secret" : "no longer secret")
            : c.Field == "Build time" ? (c.To == "on" ? "passed to the build" : "no longer passed to the build") : c.Field.ToLowerInvariant()).ToList());

    private static AuditEntry Entry(AppSummary app, int? appId, string action, string summary, IReadOnlyList<AuditChange> changes, AuditOutcome outcome, string? target = null) =>
        new(action, summary)
        {
            AppId = appId ?? app.AppId, AppName = app.Title, Container = app.Container, Changes = changes, Outcome = outcome, Target = target,
        };

    private static string Lower(string label) => label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;

    private static string Reason(string error) => AuditLog.Clean(error, 300)?.TrimEnd('.') ?? "";
}
