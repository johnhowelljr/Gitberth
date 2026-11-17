using System.Text;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Network;

namespace Gitberth.Components.Shared;

/// <summary>
/// The tips the badges and statuses give themselves, by what they show: an app's state (status.running), a deploy's
/// (status.deploy.failed), an address's access (badge.access.lan), an app's source (badge.source.git) and its
/// auto-deploy (badge.watch.on). <see cref="Computed"/> lists every one, so the catalog's tests know they're used.
/// </summary>
public static class TipKeys
{
    public static string State(AppState state) => "status." + Kebab(state.ToString());

    public static string Deploy(DeployStatus status) => "status.deploy." + Kebab(status.ToString());

    public static string Access(AddressKind kind) => "badge.access." + Kebab(kind.ToString());

    public static string Source(AppSource source) => "badge.source." + Kebab(source.ToString());

    /// <summary>A Git app's auto-deploy: on, off, paused while the app is stopped, or on but the repo can't be read.</summary>
    public static readonly IReadOnlyList<string> Watch = ["badge.watch.on", "badge.watch.off", "badge.watch.paused", "badge.watch.error"];

    /// <summary>Every key the components above compute at run time (the catalog must have each one).</summary>
    public static IEnumerable<string> Computed =>
        Enum.GetValues<AppState>().Select(State)
            .Concat(Enum.GetValues<DeployStatus>().Select(Deploy))
            .Concat(Enum.GetValues<AddressKind>().Select(Access))
            .Concat(Enum.GetValues<AppSource>().Select(Source))
            .Concat(Watch);

    /// <summary>RolledBack → rolled-back, HostOnly → host-only.</summary>
    public static string Kebab(string name)
    {
        var text = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) text.Append('-');
            text.Append(char.ToLowerInvariant(name[i]));
        }
        return text.ToString();
    }
}
