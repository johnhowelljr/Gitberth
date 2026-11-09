namespace Gitberth.Detection;

/// <param name="Version">The denoland/deno image tag, a full version like 2.9.8.</param>
/// <param name="InstallConfig">Runs `deno install` for deno.json's import map and package.json's packages.</param>
/// <param name="Entry">The file whose imports are downloaded at build time, or null when there's none.</param>
/// <param name="Deno1">The image is Deno 1, which caches a file's imports with `deno cache` and has no project install.</param>
/// <param name="Build">Runs `deno task build` (Fresh needs it).</param>
/// <param name="Start">The CMD, in exec form.</param>
/// <param name="Port">The container port, or null for a worker.</param>
/// <param name="Env">ENV values for the image, like PORT=8000.</param>
internal sealed record DenoBuild(string Version, bool InstallConfig, string? Entry, bool Deno1, bool Build, IReadOnlyList<string> Start, int? Port, IReadOnlyList<string> Env);

/// <summary>The Dockerfile Gitberth generates for a Deno app.</summary>
internal static class DenoDockerfiles
{
    public const string Ignore = ".git\n**/node_modules\n";

    /// <summary>
    /// One stage on the official image, which runs as root with its module cache in /deno-dir. The dependencies are
    /// downloaded into that cache while building, so they're part of the image and the app starts without fetching them.
    /// </summary>
    public static string For(DenoBuild b)
    {
        var downloads = new List<string>();
        if (b.InstallConfig && !b.Deno1) downloads.Add("deno install");
        if (b.Entry is not null) downloads.Add(b.Deno1 ? $"deno cache {b.Entry}" : $"deno install --entrypoint {b.Entry}");
        return Lines(
            $"FROM denoland/deno:{b.Version}",
            "WORKDIR /app",
            "COPY . .",
            DockerfileMarkers.BuildArgs,
            downloads.Count > 0 ? "# The dependencies download now, into the image, so the app starts without fetching them." : null,
            downloads.Count > 0 ? $"RUN {string.Join(" && ", downloads)}" : null,
            b.Build ? "RUN deno task build" : null,
            b.Env.Count > 0 ? $"ENV {string.Join(' ', b.Env)}" : null,
            DockerfileMarkers.Volumes,
            b.Port is int port ? $"EXPOSE {port}" : null,
            $"CMD {Json(b.Start)}");
    }

    private static string Json(IEnumerable<string> args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Lines(params IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}
