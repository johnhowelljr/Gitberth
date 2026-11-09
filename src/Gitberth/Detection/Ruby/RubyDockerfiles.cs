namespace Gitberth.Detection;

/// <summary>How a Ruby app is built and started.</summary>
/// <param name="Tag">The ruby image's version: 3.4, or 3.4.1 when the Gemfile pins it.</param>
/// <param name="Env">Settings both stages share, like RAILS_ENV=production.</param>
/// <param name="BuildPackages">apt packages that gems need to compile, besides the compilers.</param>
/// <param name="RuntimePackages">apt packages that gems need when the app runs, like postgresql-client for pg.</param>
/// <param name="Locked">Gemfile.lock is committed, so Bundler installs exactly what it lists (BUNDLE_DEPLOYMENT).</param>
/// <param name="AddLinuxPlatforms">Gemfile.lock lists no Linux platform, so the build adds them to its copy first.</param>
/// <param name="BundleHash">Names the gem cache, so it holds exactly this bundle.</param>
/// <param name="Node">The package.json whose dependencies install before the asset steps, if any.</param>
/// <param name="Steps">RUN lines that build assets, after the gems and the front end's packages are in.</param>
/// <param name="Port">The port the app listens on; null for a worker.</param>
/// <param name="Start">The command sh runs at start; it ends by exec-ing the server.</param>
internal sealed record RubyPlan(
    string Tag, IReadOnlyList<string> Env, IReadOnlyList<string> BuildPackages, IReadOnlyList<string> RuntimePackages,
    bool Locked, bool AddLinuxPlatforms, string BundleHash, NodeAssets? Node, IReadOnlyList<string> Steps, int? Port, string Start);

internal static class RubyDockerfiles
{
    public const string Ignore = ".git\n**/node_modules\n/log/*\n/tmp/*\n/vendor/bundle\n";

    /// <summary>Always in the build stage: Rails' own Dockerfile installs the same.</summary>
    private const string Compilers = "build-essential git pkg-config libyaml-dev";

    /// <summary>
    /// Two stages: gems compile in a build stage with the compilers, and the final image gets only the installed gems and
    /// the app. Gems install into a cache named after Gemfile.lock, because Gitberth's build arguments (the commit, for
    /// one) change on every deploy and would otherwise make every build compile them again. Inside it each Debian release
    /// gets its own folder, so extensions built against one release's libraries never land on the next.
    /// </summary>
    public static string Render(RubyPlan p) => Lines(
    [
        $"FROM ruby:{p.Tag}-slim AS base",
        "WORKDIR /app",
        p.RuntimePackages.Count == 0 ? null : AptInstall(string.Join(' ', p.RuntimePackages)),
        "# Bundler leaves out the development and test gems. MALLOC_ARENA_MAX=2 keeps a threaded Ruby server's memory down.",
        "ENV " + string.Join(" \\\n    ", Rows(
        [
            "BUNDLE_PATH=/usr/local/bundle", "BUNDLE_WITHOUT=development:test", p.Locked ? "BUNDLE_DEPLOYMENT=1" : null, .. p.Env,
        ])),
        "",
        "FROM base AS build",
        "# Compilers and headers for gems with native extensions.",
        AptInstall(string.Join(' ', new[] { Compilers }.Concat(p.BuildPackages))),
        .. p.Node?.Setup() ?? [],
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        p.AddLinuxPlatforms ? "# Gemfile.lock lists no Linux platform, so this build's copy of it gets them.\nRUN BUNDLE_DEPLOYMENT=false bundle lock --add-platform x86_64-linux aarch64-linux" : null,
        $"# The gems install into a cache kept for this {(p.Locked ? "Gemfile.lock" : "Gemfile")} and Debian release, so their native extensions compile once.",
        $"RUN --mount=type=cache,id=gems-{p.Tag}-{p.BundleHash},target=/gems \\",
        "    gems=/gems/$(. /etc/os-release && echo \"$VERSION_CODENAME\") && \\",
        "    BUNDLE_PATH=$gems bundle install && \\",
        "    cp -a $gems/. /usr/local/bundle/ && \\",
        "    rm -rf /usr/local/bundle/ruby/*/cache /usr/local/bundle/ruby/*/bundler/gems/*/.git",
        p.Node?.Install,
        .. p.Steps,
        "",
        "FROM base",
        "COPY --from=build /usr/local/bundle /usr/local/bundle",
        "COPY --from=build /app /app",
        p.Port is null ? null : $"ENV PORT={p.Port}",
        DockerfileMarkers.Volumes,
        p.Port is null ? null : $"EXPOSE {p.Port}",
        $"CMD {Json("sh", "-c", p.Start)}",
    ]);

    private static string AptInstall(string packages) =>
        $"RUN apt-get update -qq && \\\n    apt-get install --no-install-recommends -y {packages} && \\\n    rm -rf /var/lib/apt/lists /var/cache/apt/archives";

    // Three settings per line, so a long ENV stays readable.
    private static IEnumerable<string> Rows(IEnumerable<string?> settings) =>
        settings.OfType<string>().Chunk(3).Select(chunk => string.Join(' ', chunk));

    internal static string Json(params string[] args) =>
        "[" + string.Join(", ", args.Select(a => "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    internal static string Lines(IEnumerable<string?> lines) => string.Join('\n', lines.Where(l => l is not null)) + "\n";
}
