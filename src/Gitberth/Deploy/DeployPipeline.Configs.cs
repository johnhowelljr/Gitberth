using System.Text.Json.Nodes;
using Gitberth.Data;

namespace Gitberth.Deploy;

/// <summary>
/// An app's config files (B3) in its deploys: each written from Gitberth's database and mounted read-only at its path, in
/// the container a deploy starts (and its release command's), or in its Compose service. Adding or removing one takes
/// effect here, at the app's next deploy.
/// </summary>
public sealed partial class DeployPipeline
{
    private async Task<IReadOnlyList<VolumeMount>> ConfigMountsAsync(AppDefinition app, DeployLog log, bool quiet = false)
    {
        if (configFiles is null) return [];
        try
        {
            var mounts = await configFiles.MountsAsync(app, null, CancellationToken.None);
            if (mounts.Count > 0 && !quiet) log.Line($"Its config files, mounted read-only: {string.Join(", ", mounts.Select(m => m.Target))}.");
            return mounts;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Line($"Its config files couldn't be written to Gitberth's data folder ({ex.Message.TrimEnd('.')}), so it starts without them.");
            return [];
        }
    }

    /// <summary>A Compose app's config files: each added to its service's volumes in the file the release runs.</summary>
    private async Task AddComposeConfigMountsAsync(AppDefinition app, JsonObject model, DeployLog log)
    {
        if (configFiles is null || model["services"] is not JsonObject services) return;
        IReadOnlyDictionary<string, IReadOnlyList<VolumeMount>> byService;
        try
        {
            byService = await configFiles.ComposeMountsAsync(app, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Line($"Its config files couldn't be written to Gitberth's data folder ({ex.Message.TrimEnd('.')}), so its services start without them.");
            return;
        }
        foreach (var (service, mounts) in byService)
        {
            if (mounts.Count == 0) continue;
            if (services[service] is not JsonObject definition)
            {
                log.Line($"Its config files for {service} aren't mounted: the Compose file has no service called {service}.");
                continue;
            }
            if (definition["volumes"] is not JsonArray volumes)
            {
                volumes = [];
                definition["volumes"] = volumes;
            }
            // One mount at a path: the config file takes the place of whatever the file mounted there.
            foreach (var m in mounts)
            {
                foreach (var old in volumes.OfType<JsonObject>().Where(v => (string?)v["target"] == m.Target).ToList()) volumes.Remove(old);
                volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = m.Source, ["target"] = m.Target, ["read_only"] = true });
            }
            log.Line($"{service}'s config files, mounted read-only: {string.Join(", ", mounts.Select(m => m.Target))}.");
        }
    }
}
