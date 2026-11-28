using Gitberth.Data;
using Gitberth.Docker;

namespace Gitberth.Deploy;

/// <summary>
/// A deploy's release command (B2): once the new version's image is there and before it takes over, the app's release
/// command runs in a one-off container of it, with the variables, volumes and networks the new version gets (no ports, no
/// names on its networks, no restart policy). One that fails or runs too long fails the deploy, and the running version
/// stays: nothing of it was touched.
/// </summary>
public sealed partial class DeployPipeline
{
    private ReleaseCommands? _ownCommands;

    private ReleaseCommands Commands => commands ?? (_ownCommands ??= new ReleaseCommands(dbFactory,
        new OneOffRunner(docker, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<OneOffRunner>.Instance),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ReleaseCommands>.Instance));

    /// <summary>The release command of a Git or uploaded app's new version (one container), in a one-off container of <paramref name="image"/>.</summary>
    private async Task ReleaseAsync(AppDefinition app, string image, List<EnvValue> env, bool railpack, DeployLog log, CancellationToken ct)
    {
        var (_, there, ids) = await NetworksAsync(app, log);
        var (gatewayPriority, _) = await HostTraitsAsync();
        // As the swap would start it, with none of the app's names or addresses on its networks (as a deploy's temporary
        // container has them), no ports and no restart policy.
        var spec = ContainerSpecFactory.For(app, image, env, options.Network, ports: [], restartPolicy: "no", bridge: true, networks: there,
            gatewayPriority: gatewayPriority, networkIds: ids, railpackImage: railpack, configMounts: await ConfigMountsAsync(app, log, quiet: true));
        await Commands.RunForDeployAsync(app, spec, log, ct);
    }

    /// <summary>
    /// Whether a Compose app's deploy runs its release command: a build (or a pull) of its release, or a rollback to another
    /// release; never a restart or a deploy that applies settings with the same images.
    /// </summary>
    private static bool RunsComposeRelease(AppDefinition app, DeployRequest request, Deployment dep) =>
        app.ReleaseCommand is { Length: > 0 } && (request.Build || dep.Trigger == DeployTrigger.Rollback);
}
