namespace Gitberth.Detection;

/// <summary>How the modules reach the build.</summary>
internal enum GoModules
{
    /// <summary>The standard library only: nothing to download.</summary>
    None,

    /// <summary>`go mod download` from go.mod and go.sum, in a layer of its own.</summary>
    Download,

    /// <summary>From the repo's vendor/ folder.</summary>
    Vendor,

    /// <summary>The repo has no go.sum: `go mod tidy` writes it during the build.</summary>
    Tidy,
}

/// <param name="Image">The golang image tag, like "1.25".</param>
/// <param name="Package">What `go build` builds: "." or "./cmd/server".</param>
/// <param name="Binary">The binary's file name. It goes in /app, next to the repo's other files.</param>
/// <param name="Start">The CMD, in exec form.</param>
/// <param name="Port">The container port, or null for a worker.</param>
/// <param name="Env">ENV values for the image, like PORT=8080.</param>
internal sealed record GoBuild(string Image, GoModules Modules, bool Cgo, string Package, string Binary, IReadOnlyList<string> Start, int? Port, IReadOnlyList<string> Env);

/// <summary>The Dockerfile Gitberth generates for a Go module.</summary>
internal static class GoDockerfiles
{
    public const string Ignore = ".git\n**/node_modules\n";

    private const string ModuleCache = "--mount=type=cache,id=gomod,target=/go/pkg/mod";
    private const string BuildCache = "--mount=type=cache,id=gobuild,target=/root/.cache/go-build";

    /// <summary>
    /// Built in a golang image, with the module and build caches kept between builds. A static binary runs on Alpine; one
    /// built with cgo needs glibc, so it runs on Debian slim. The newest Debian, since a binary built on an older glibc runs
    /// on a newer one. Either way the image gets CA certificates and time zones, which Go apps need for HTTPS and time.LoadLocation.
    /// </summary>
    public static string For(GoBuild b)
    {
        var download = b.Modules == GoModules.Download;
        var vendor = b.Modules == GoModules.Vendor;
        var tidy = b.Modules == GoModules.Tidy;
        return Lines(
            $"FROM golang:{b.Image} AS build",
            "WORKDIR /app",
            "# Go downloads the toolchain go.mod or a module asks for, when it's newer than this image's.",
            "ENV GOTOOLCHAIN=auto",
            download ? "# The modules download first, in a layer that's reused until go.mod or go.sum change." : null,
            download ? "COPY go.mod go.sum ./" : null,
            download ? $"RUN {ModuleCache} go mod download" : null,
            "COPY . .",
            DockerfileMarkers.BuildArgs,
            tidy ? "# The repo has no go.sum, so go mod tidy writes it, checking each module against sum.golang.org." : null,
            $"RUN {ModuleCache} \\",
            $"    {BuildCache} \\",
            $"    {(tidy ? "go mod tidy && " : "")}CGO_ENABLED={(b.Cgo ? 1 : 0)} go build{(vendor ? " -mod=vendor" : "")} -trimpath -ldflags=\"-s -w\" -o /out/{b.Binary} {b.Package}",
            "# The repo's other files (templates/, static/, migrations/...) stay, laid out as in the repo: apps often read them at run time.",
            $"RUN {(vendor ? "rm -rf vendor && " : "")}find . -name '*.go' -delete && find . -mindepth 1 -type d -empty -delete",
            "",
            b.Cgo ? "FROM debian:trixie-slim" : "FROM alpine:3",
            b.Cgo
                ? "RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates tzdata && rm -rf /var/lib/apt/lists/*"
                : "RUN apk add --no-cache ca-certificates tzdata",
            "WORKDIR /app",
            "COPY --from=build /app/ ./",
            $"COPY --from=build /out/{b.Binary} ./",
            b.Env.Count > 0 ? $"ENV {string.Join(' ', b.Env)}" : null,
            DockerfileMarkers.Volumes,
            b.Port is int port ? $"EXPOSE {port}" : null,
            $"CMD {Json(b.Start)}");
    }

    internal static string Json(IEnumerable<string> args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Lines(params IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}
