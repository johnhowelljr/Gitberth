using System.Text;

namespace Gitberth.Detection;

/// <summary>
/// The built-in Dockerfiles. They contain two marker comments that Gitberth expands at build time
/// (see <see cref="DockerfileMarkers"/>); left unexpanded they are plain comments.
/// </summary>
public static class DockerfileTemplates
{
    /// <summary>Newest GA .NET major; newer target frameworks use the -preview image tags.</summary>
    public const int LatestDotNet = 10;

    public const string DotNetIgnore = ".git\n**/bin\n**/obj\n.vs\n**/node_modules\n";
    public const string NodeIgnore = ".git\n**/node_modules\n**/.next\n**/.nuxt\n**/.output\n**/.svelte-kit\n";
    public const string StaticIgnore = ".git\n";

    private const string SpaNginx = """
        COPY <<'EOF' /etc/nginx/conf.d/default.conf
        server {
            listen 80;
            root /usr/share/nginx/html;
            index index.html;
            location / {
                try_files $uri $uri/ /index.html;
            }
        }
        EOF
        """;

    private const string StaticNginx = """
        COPY <<'EOF' /etc/nginx/conf.d/default.conf
        server {
            listen 80;
            root /usr/share/nginx/html;
            index index.html;
            location / {
                try_files $uri $uri.html $uri/ =404;
            }
        }
        EOF
        """;

    /// <summary>Starting point when nothing was detected.</summary>
    public const string Starter = """
        # Gitberth couldn't detect how to build this repo. Describe it here.
        FROM alpine:3
        WORKDIR /app
        COPY . .
        # gitberth:build-args
        # RUN <your build command>
        # gitberth:volumes
        EXPOSE 8080
        CMD ["sh", "-c", "echo 'Replace this CMD with your start command' && sleep infinity"]
        """;

    private static readonly string NodeInSdkImage = Lines(
        $"COPY --from=node:{NodeVersions.DefaultMajor}-slim /usr/local/bin/node /usr/local/bin/node",
        $"COPY --from=node:{NodeVersions.DefaultMajor}-slim /usr/local/lib/node_modules /usr/local/lib/node_modules",
        "RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx").TrimEnd();

    public static string DotNetTag(TargetFrameworkInfo tfm) => tfm.Major > LatestDotNet ? $"{tfm.Version}-preview" : tfm.Version;

    public static string Static(string siteDir) => Lines(
        "FROM nginx:stable-alpine",
        StaticNginx,
        $"COPY {(siteDir.Length == 0 ? "." : siteDir)}/ /usr/share/nginx/html/",
        DockerfileMarkers.Volumes,
        "EXPOSE 80");

    public static string DotNetWeb(string tag, int major, string project, string assembly, bool needsNode) => Lines(
        $"FROM mcr.microsoft.com/dotnet/sdk:{tag} AS build",
        "WORKDIR /src",
        needsNode ? NodeInSdkImage : null,
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        "RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \\",
        $"    dotnet publish \"{project}\" -c Release -o /app/publish -p:UseAppHost=false",
        "",
        $"FROM mcr.microsoft.com/dotnet/aspnet:{tag}",
        "WORKDIR /app",
        "COPY --from=build /app/publish .",
        major >= 8 ? "ENV ASPNETCORE_HTTP_PORTS=8080" : "ENV ASPNETCORE_URLS=http://+:8080",
        "EXPOSE 8080",
        DockerfileMarkers.Volumes,
        major >= 8 ? "USER $APP_UID" : null,
        $"ENTRYPOINT [\"dotnet\", \"{assembly}.dll\"]");

    public static string DotNetWorker(string tag, int major, string project, string assembly, bool usesAspNetCore) => Lines(
        $"FROM mcr.microsoft.com/dotnet/sdk:{tag} AS build",
        "WORKDIR /src",
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        "RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \\",
        $"    dotnet publish \"{project}\" -c Release -o /app/publish -p:UseAppHost=false",
        "",
        $"FROM mcr.microsoft.com/dotnet/{(usesAspNetCore ? "aspnet" : "runtime")}:{tag}",
        "WORKDIR /app",
        "COPY --from=build /app/publish .",
        DockerfileMarkers.Volumes,
        major >= 8 ? "USER $APP_UID" : null,
        $"ENTRYPOINT [\"dotnet\", \"{assembly}.dll\"]");

    public static string DotNetWasm(string tag, string project) => Lines(
        $"FROM mcr.microsoft.com/dotnet/sdk:{tag} AS build",
        "WORKDIR /src",
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        "RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \\",
        $"    dotnet publish \"{project}\" -c Release -o /app/publish",
        "",
        "FROM nginx:stable-alpine",
        SpaNginx,
        "COPY --from=build /app/publish/wwwroot/ /usr/share/nginx/html/",
        DockerfileMarkers.Volumes,
        "EXPOSE 80");

    /// <summary>Front end built with Node, served by nginx.</summary>
    internal static string NodeStatic(int node, NodeBuild b, string outDir, bool spaFallback) => Lines(
    [
        .. BuildStage(node, b, "build"),
        "",
        "FROM nginx:stable-alpine",
        spaFallback ? SpaNginx : StaticNginx,
        $"COPY --from=build {b.WorkDir}/{outDir.Trim('/')}/ /usr/share/nginx/html/",
        DockerfileMarkers.Volumes,
        "EXPOSE 80",
    ]);

    /// <summary>A Node or Bun server, in one stage: it keeps everything it was built with.</summary>
    /// <param name="env">More ENV settings the framework needs, like "LOG_LEVEL=info".</param>
    /// <param name="runDir">The folder to start in, when it isn't the app's own (AdonisJS runs from build/).</param>
    internal static string NodeServer(int node, NodeBuild b, string[] start, int port, string? env = null, string? runDir = null) => Lines(
    [
        .. BuildStage(node, b, null),
        $"ENV NODE_ENV=production HOST=0.0.0.0 PORT={port}{(env is null ? "" : " " + env)}",
        DockerfileMarkers.Volumes,
        $"EXPOSE {port}",
        runDir is null ? null : $"WORKDIR {runDir}",
        $"CMD {Json(start)}",
    ]);

    /// <summary>Only for an app at the repo root: standalone output nests sub-folder apps under their path.</summary>
    internal static string NextStandalone(int node, NodeBuild b, bool hasPublicDir) => Lines(
    [
        .. BuildStage(node, b, "build", "ENV NEXT_TELEMETRY_DISABLED=1"),
        "",
        $"FROM node:{node}-slim",
        "WORKDIR /app",
        "ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 HOSTNAME=0.0.0.0 PORT=3000",
        "COPY --from=build /app/.next/standalone ./",
        "COPY --from=build /app/.next/static ./.next/static",
        hasPublicDir ? "COPY --from=build /app/public ./public" : null,
        DockerfileMarkers.Volumes,
        "EXPOSE 3000",
        "CMD [\"node\", \"server.js\"]",
    ]);

    internal static string NextServer(int node, NodeBuild b, bool hasStartScript) => Lines(
    [
        .. BuildStage(node, b, null, "ENV NEXT_TELEMETRY_DISABLED=1"),
        "ENV NODE_ENV=production HOSTNAME=0.0.0.0 PORT=3000",
        DockerfileMarkers.Volumes,
        "EXPOSE 3000",
        $"CMD {Json(hasStartScript ? b.Pm.StartCommand : ["npx", "next", "start"])}",
    ]);

    internal static string Nuxt(int node, NodeBuild b) => Lines(
    [
        .. BuildStage(node, b, "build"),
        "",
        $"FROM node:{node}-slim",
        "WORKDIR /app",
        "ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000",
        $"COPY --from=build {b.WorkDir}/.output ./.output",
        DockerfileMarkers.Volumes,
        "EXPOSE 3000",
        "CMD [\"node\", \".output/server/index.mjs\"]",
    ]);

    internal static string SvelteKitNode(int node, NodeBuild b) => Lines(
    [
        .. BuildStage(node, b, null),
        "ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000",
        DockerfileMarkers.Volumes,
        "EXPOSE 3000",
        "CMD [\"node\", \"build\"]",
    ]);

    /// <summary>
    /// The whole repo is copied, so an app in a sub-folder can reach its neighbours (`cd ..`, "file:../server").
    /// Installs and the build then run from the app's folder. An app that runs on Bun builds on Bun's image; one that only
    /// installs with bun gets bun's binary in the Node image.
    /// </summary>
    private static IEnumerable<string?> BuildStage(int node, NodeBuild b, string? stage, string? env = null) =>
    [
        // Tells a custom Dockerfile started from this one to keep building from the repo root.
        b.AppDir.Length > 0 ? DockerfileMarkers.RepoRootContext : null,
        $"FROM {(b.OnBun ? $"oven/bun:{b.Bun ?? BunVersions.DefaultTag}" : $"node:{node}-slim")}{(stage is null ? "" : " AS " + stage)}",
        "WORKDIR /app",
        env,
        b.UsesCorepack ? "ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0\nRUN npm install -g corepack@latest && corepack enable" : null,
        b.Bun is { } bun && !b.OnBun ? BunInNodeImage(bun) : null,
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        b.AppDir.Length > 0 ? $"WORKDIR {b.WorkDir}" : null,
        b.BinOnPath ? $"ENV PATH={BinPath(b.AppDir)}:$PATH" : null,
        .. b.Installs,
        b.Build is null ? null : $"RUN {b.Build}",
    ];

    /// <summary>bun's single binary, from its own image, for an app that installs with bun and runs on Node.</summary>
    private static string BunInNodeImage(string tag) => Lines(
        "# bun installs the dependencies from its lockfile; Node runs the app.",
        $"COPY --from=oven/bun:{tag} /usr/local/bin/bun /usr/local/bin/bun",
        "RUN ln -s bun /usr/local/bin/bunx").TrimEnd();

    /// <summary>Like Vercel: the node_modules/.bin of the app's folder and of each folder above it, up to the repo root.</summary>
    internal static string BinPath(string appDir)
    {
        var segments = appDir.Length == 0 ? [] : appDir.Split('/');
        return string.Join(':', Enumerable.Range(0, segments.Length + 1).Reverse()
            .Select(n => "/app" + string.Concat(segments.Take(n).Select(s => "/" + s)) + "/node_modules/.bin"));
    }

    private static string Json(IEnumerable<string> args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Lines(params IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}

/// <summary>Marker comments in generated or custom Dockerfiles that Gitberth fills in at build time.</summary>
public static class DockerfileMarkers
{
    public const string BuildArgs = "# gitberth:build-args";
    public const string Volumes = "# gitberth:volumes";

    /// <summary>In a custom Dockerfile for an app in a sub-folder: build from the repo root instead of that folder.</summary>
    public const string RepoRootContext = "# gitberth:context repo-root";

    /// <summary>
    /// The build arg declared where secrets are mounted, whose value changes with theirs (Deploy.BuildSecrets): BuildKit
    /// leaves secret mounts out of its build cache, so without it a step would reuse what it built with an older value.
    /// </summary>
    public const string SecretsArg = "GITBERTH_SECRETS";

    /// <summary>
    /// Build-time variables become ARG lines. Secret ones are mounted into every RUN from there to the stage's end instead
    /// (`RUN --mount=type=secret,id=KEY,env=KEY`): the steps see them as variables, and the image history never records
    /// them. With secrets, <see cref="SecretsArg"/> is declared there too, so those steps are cached by the secrets'
    /// values. Volume targets are created and handed to the image's app user ($APP_UID in .NET images, root elsewhere),
    /// so a fresh named volume is writable by the app.
    /// </summary>
    public static string Expand(string dockerfile, IReadOnlyCollection<string> buildArgs, IReadOnlyCollection<string> volumeTargets,
        IReadOnlyCollection<string>? secrets = null)
    {
        var sb = new StringBuilder();
        var mounts = string.Concat((secrets ?? []).Select(k => $"--mount=type=secret,id={k},env={k} "));
        var inSecretStage = false;
        foreach (var line in dockerfile.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)) inSecretStage = false;
            if (inSecretStage && mounts.Length > 0 && trimmed.StartsWith("RUN ", StringComparison.OrdinalIgnoreCase))
            {
                var at = line.IndexOf("RUN ", StringComparison.OrdinalIgnoreCase) + 4;
                sb.Append(line[..at]).Append(mounts).Append(line[at..]).Append('\n');
            }
            else if (trimmed == BuildArgs)
            {
                foreach (var key in buildArgs) sb.Append("ARG ").Append(key).Append('\n');
                if (mounts.Length > 0) sb.Append("ARG ").Append(SecretsArg).Append('\n');
                inSecretStage = true;
            }
            else if (trimmed == Volumes)
            {
                if (volumeTargets.Count == 0) continue;
                var paths = string.Join(' ', volumeTargets.Select(ShellQuote));
                sb.Append("RUN mkdir -p ").Append(paths).Append(" && chown -R \"${APP_UID:-0}\" ").Append(paths).Append('\n');
            }
            else
            {
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Whether <see cref="Expand"/> mounts secret values into this Dockerfile (as written, with its marker lines). Without
    /// the build-args line, values can only go in as build args for the ARGs it declares itself.
    /// </summary>
    public static bool TakesSecrets(string dockerfile) =>
        dockerfile.Replace("\r\n", "\n").Split('\n').Any(line => line.Trim() == BuildArgs);

    /// <summary>The ARGs a Dockerfile declares before its first FROM: the ones that choose base images and tool versions.</summary>
    public static IReadOnlySet<string> GlobalArgs(string dockerfile)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in dockerfile.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)) break;
            if (!trimmed.StartsWith("ARG ", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var part in trimmed[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                names.Add(part.Split('=')[0]);
        }
        return names;
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
