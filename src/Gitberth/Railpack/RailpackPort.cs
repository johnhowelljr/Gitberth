using System.Globalization;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Services;

namespace Gitberth.Railpack;

/// <summary>
/// PORT for the container of an image Railpack built: Railpack's start commands, and apps written for Railway, listen on
/// it. It's the app's web container port, the one Gitberth sends visitors to, given when the container is created, so a
/// port change needs no new build.
/// </summary>
public static class RailpackPort
{
    /// <summary>
    /// The variables with PORT=&lt;web container port&gt; added, unless the app's own variables set PORT; as they are without
    /// a web port. A repo's GitHub PORT never gets here: Gitberth sets where an app listens.
    /// </summary>
    public static List<EnvValue> With(IReadOnlyList<EnvValue> env, IReadOnlyList<PortMapping> ports)
    {
        var list = env.ToList();
        if (Added(env, ports) is int port) list.Add(new EnvValue("PORT", port.ToString(CultureInfo.InvariantCulture), IsSecret: false));
        return list;
    }

    /// <summary>The PORT <see cref="With"/> adds; null when it adds none.</summary>
    public static int? Added(IReadOnlyList<EnvValue> env, IReadOnlyList<PortMapping> ports) =>
        env.Any(e => e.Key == "PORT") || PortRules.WebOf(ports) is not { } web ? null : web.ContainerPort;
}
