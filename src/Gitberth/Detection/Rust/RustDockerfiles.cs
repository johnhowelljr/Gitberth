using System.Globalization;
using System.Text.RegularExpressions;

namespace Gitberth.Detection;

/// <param name="Image">The rust image tag, like "1.99".</param>
/// <param name="Locked">Cargo.lock is committed, so the build uses exactly its versions.</param>
/// <param name="Package">The workspace member to build (`-p`), or null for the app's own crate.</param>
/// <param name="Binary">The binary target, as Cargo names its file.</param>
/// <param name="OutputDir">Where Cargo writes it: target/release, or target/&lt;triple&gt;/release for a configured target.</param>
/// <param name="Target">A target triple .cargo/config.toml builds for, which the toolchain needs added; null for the host's.</param>
/// <param name="RuntimeName">Its name in /app, next to the repo's other files.</param>
/// <param name="Packages">Debian packages the binary needs at run time, besides CA certificates.</param>
/// <param name="Start">The CMD, in exec form.</param>
/// <param name="Port">The container port, or null for a worker.</param>
/// <param name="Env">ENV values for the image, like PORT=3000.</param>
internal sealed record RustBuild(
    string Image, bool Locked, string? Package, string Binary, string OutputDir, string? Target, string RuntimeName,
    IReadOnlyList<string> Packages, IReadOnlyList<string> Start, int? Port, IReadOnlyList<string> Env);

/// <summary>The Dockerfile Gitberth generates for a Rust crate or workspace.</summary>
internal static class RustDockerfiles
{
    public const string Ignore = ".git\n**/target\n**/node_modules\n";

    /// <summary>
    /// Built in the full rust image, which has the C headers crates like openssl-sys and pq-sys build against. The registry,
    /// git checkouts and target folder are caches kept between builds, so the binary is copied out of target/ before its
    /// step ends. It runs on Debian slim for glibc, the newest release, since a binary built on an older glibc runs on a newer one.
    /// </summary>
    public static string For(RustBuild b) => Lines(
        $"FROM rust:{b.Image} AS build",
        "WORKDIR /app",
        "COPY . .",
        // After the copy, so it goes to the toolchain a rust-toolchain.toml picks.
        b.Target is null ? null : $"RUN rustup target add {b.Target}",
        DockerfileMarkers.BuildArgs,
        "# target/ is a cache kept between builds, so the binary is copied out of it in the same step.",
        "RUN --mount=type=cache,id=cargo-registry,target=/usr/local/cargo/registry \\",
        "    --mount=type=cache,id=cargo-git,target=/usr/local/cargo/git \\",
        $"    --mount=type=cache,id=cargo-target-{b.Binary},target=/app/target \\",
        $"    cargo build --release{(b.Locked ? " --locked" : "")}{(b.Package is null ? "" : $" -p {b.Package}")} --bin {b.Binary} \\",
        $"    && mkdir -p /out && cp {b.OutputDir}/{b.Binary} /out/",
        "# The repo's other files (templates/, static/, Rocket.toml...) stay, laid out as in the repo: apps often read them at run time.",
        "RUN find . -name '*.rs' -delete && find . -mindepth 1 -type d -empty -delete",
        "",
        "FROM debian:trixie-slim",
        // ca-certificates pulls in openssl, and with it libssl, which crates built on openssl-sys link.
        $"RUN apt-get update && apt-get install -y --no-install-recommends {string.Join(' ', b.Packages.Prepend("ca-certificates"))} && rm -rf /var/lib/apt/lists/*",
        "WORKDIR /app",
        "COPY --from=build /app/ ./",
        $"COPY --from=build /out/{b.Binary} ./{(b.RuntimeName == b.Binary ? "" : b.RuntimeName)}",
        b.Env.Count > 0 ? $"ENV {string.Join(' ', b.Env)}" : null,
        DockerfileMarkers.Volumes,
        b.Port is int port ? $"EXPOSE {port}" : null,
        $"CMD {Json(b.Start)}");

    private static string Json(IEnumerable<string> args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    private static string Lines(params IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}

/// <summary>Which rust image builds a crate.</summary>
internal static partial class RustImages
{
    /// <summary>A current stable Rust that Gitberth has checked a rust image for. Bump it now and then.</summary>
    public const string Default = "1.99";

    /// <param name="Tag">The rust image tag.</param>
    /// <param name="Source">Where the version came from, for the evidence.</param>
    /// <param name="Channel">A nightly or beta channel rust-toolchain.toml names, which rustup installs during the build.</param>
    public sealed record Choice(string Tag, string Source, string? Channel);

    /// <summary>
    /// rust-toolchain.toml (or the older rust-toolchain file) pins the toolchain. Without one, rust-version in Cargo.toml
    /// is only the oldest Rust the crate supports, and its dependencies may need a newer one, so it only raises the default.
    /// </summary>
    public static Choice Resolve(RepoFiles files, string? rustVersion)
    {
        foreach (var name in new[] { "rust-toolchain.toml", "rust-toolchain" })
        {
            if (!files.Exists(name)) continue;
            var text = RustSources.Read(files.Full(name));
            var channel = text.Contains("[toolchain]", StringComparison.Ordinal)
                ? RustToml.Str(RustToml.Parse(text), "toolchain", "channel")
                : text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (string.IsNullOrWhiteSpace(channel)) continue;
            if (Minor(channel) is { } pinned) return new Choice(pinned, $"{name}: {channel}", null);
            if (channel.StartsWith("stable", StringComparison.Ordinal)) return new Choice(Default, $"{name}: stable", null);
            return new Choice(Default, $"{name}: {channel}", channel);
        }
        if (Minor(rustVersion) is { } minimum)
        {
            return Newer(minimum, Default)
                ? new Choice(minimum, $"rust-version {rustVersion} in Cargo.toml", null)
                : new Choice(Default, $"Gitberth's default; Cargo.toml's rust-version {rustVersion} is the oldest it supports", null);
        }
        return new Choice(Default, "Gitberth's default", null);
    }

    /// <summary>"1.82.0" or "1.82" → "1.82"; null for a channel name like "nightly".</summary>
    public static string? Minor(string? version)
    {
        var m = VersionPattern().Match(version?.Trim() ?? "");
        return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}" : null;
    }

    private static bool Newer(string a, string b)
    {
        static (int, int) Parts(string v) => (int.Parse(v.Split('.')[0], CultureInfo.InvariantCulture), int.Parse(v.Split('.')[1], CultureInfo.InvariantCulture));
        return Parts(a).CompareTo(Parts(b)) > 0;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.\d+)?(?:-[\w-]+)?$")]
    private static partial Regex VersionPattern();
}
