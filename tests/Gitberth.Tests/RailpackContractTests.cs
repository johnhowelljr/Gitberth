using Gitberth.Components.Shared;
using Gitberth.Detection;
using Gitberth.Docker;
using Gitberth.Railpack;

namespace Gitberth.Tests;

/// <summary>
/// What the Railpack builder and the UI share: each of Railpack's providers as Gitberth names it, the card New app shows
/// for a plan (and the banner when Railpack can't plan it either), the deploy log's words and the errors, the pins, and the
/// stand-in for an image without Railpack.
/// </summary>
public class RailpackContractTests
{
    private static RailpackInfo Info(string provider, string? start = null, RailpackPackage[]? packages = null,
        RailpackLog[]? logs = null, Dictionary<string, string>? metadata = null) =>
        new(RailpackRelease.Version, provider, [provider], packages ?? [], metadata ?? [], logs ?? [], start, []);

    private static RailpackPlanResult Planned(RailpackInfo info) =>
        new(true, null, null, info, """{"steps":[],"deploy":{"startCommand":"sh start.sh"}}""", ["Railpack 0.43.0", "Steps", "build: chmod +x start.sh"]);

    private static RailpackPlanResult Failed(RailpackFailure failure, string error, RailpackInfo? info = null, params string[] output) =>
        new(false, failure, error, info, null, output);

    [Fact]
    public void Each_provider_is_named_as_people_name_it()
    {
        Assert.Equal(["PHP", "Go", "Java", "Rust", "Ruby", "Elixir", "Python", "Deno", ".NET", "Node.js", "Gleam", "C/C++", "Zig", "Static site", "Shell script"],
            new[] { "php", "golang", "java", "rust", "ruby", "elixir", "python", "deno", "dotnet", "node", "gleam", "cpp", "zig", "staticfile", "shell" }
                .Select(RailpackProviders.Title));
        // Railpack writes two of them capitalised; Gitberth keeps every name in lower case.
        Assert.Equal(("dotnet", "elixir"), (RailpackProviders.Normalize("Dotnet"), RailpackProviders.Normalize("Elixir")));
        Assert.Equal((".NET", "Elixir"), (RailpackProviders.Title("Dotnet"), RailpackProviders.Title("Elixir")));
        // One Gitberth doesn't list keeps its name; none at all is the repo's own steps.
        Assert.Equal(("Bun", "Custom build"), (RailpackProviders.Title("bun"), RailpackProviders.Title("")));
    }

    [Fact]
    public void The_summary_says_what_railpack_found_with_its_framework()
    {
        Assert.Equal("Shell script with Railpack", RailpackProviders.Summary(Info("shell")));
        Assert.Equal(".NET with Railpack", RailpackProviders.Summary(Info("Dotnet")));
        Assert.Equal("PHP (Laravel) with Railpack", RailpackProviders.Summary(Info("php", metadata: new() { ["phpFramework"] = "laravel" })));
        Assert.Equal("PHP (Symfony) with Railpack", RailpackProviders.Summary(Info("php", metadata: new() { ["phpFramework"] = "Symfony" })));
        Assert.Equal("PHP with Railpack", RailpackProviders.Summary(Info("php", metadata: new() { ["phpFramework"] = "slim" })));
        Assert.Equal("Ruby (Rails) with Railpack", RailpackProviders.Summary(Info("ruby", metadata: new() { ["rubyRails"] = "true" })));
        Assert.Equal("Ruby with Railpack", RailpackProviders.Summary(Info("ruby", metadata: new() { ["rubyRails"] = "false" })));
    }

    [Fact]
    public void The_marker_files_are_the_ones_in_the_apps_own_folder()
    {
        using var repo = new TempRepo()
            .With("start.sh", "echo hi").With("mix.exs", "defmodule Hello.MixProject do end")
            .With("Shop.csproj", "<Project />").With("Admin.csproj", "<Project />")
            .With("CMakeLists.txt", "project(hello)").With("meson.build", "project('hello', 'cpp')")
            .With("tools/build.zig", "const std = @import(\"std\");");

        Assert.Equal(["start.sh"], RailpackProviders.Markers("shell", repo.Root));
        Assert.Equal(["mix.exs"], RailpackProviders.Markers("Elixir", repo.Root));
        Assert.Equal(["Admin.csproj", "Shop.csproj"], RailpackProviders.Markers("Dotnet", repo.Root));
        Assert.Equal(["CMakeLists.txt", "meson.build"], RailpackProviders.Markers("cpp", repo.Root));
        // Railpack builds the app's folder alone, so a file deeper down doesn't count.
        Assert.Empty(RailpackProviders.Markers("zig", repo.Root));
        Assert.Equal(["build.zig"], RailpackProviders.Markers("zig", Path.Combine(repo.Root, "tools")));
        Assert.Empty(RailpackProviders.Markers("bun", repo.Root));
        Assert.Empty(RailpackProviders.Markers("shell", Path.Combine(repo.Root, "missing")));
    }

    [Fact]
    public void A_railpack_apps_logo_comes_from_its_summary()
    {
        Assert.Equal("gnubash", RailpackProviders.LogoSlug("Shell script with Railpack"));
        // A framework has its language's logo.
        Assert.Equal(("php", "ruby"), (RailpackProviders.LogoSlug("PHP (Laravel) with Railpack"), RailpackProviders.LogoSlug("Ruby (Rails) with Railpack")));
        Assert.Equal(["nodedotjs", "dotnet", "cplusplus", "html5", "elixir", "gleam", "zig", "go", "openjdk"],
            new[] { "Node.js", ".NET", "C/C++", "Static site", "Elixir", "Gleam", "Zig", "Go", "Java" }.Select(t => RailpackProviders.LogoSlug(t + " with Railpack")));
        Assert.Null(RailpackProviders.LogoSlug("Custom build with Railpack"));
        Assert.Null(RailpackProviders.LogoSlug("Unknown project type"));
        Assert.Null(RailpackProviders.LogoSlug(null));
    }

    [Fact]
    public void A_tool_is_named_as_people_name_it()
    {
        Assert.Equal(["Node.js", "Erlang", "Elixir", "Python", "Go", ".NET", "Caddy"],
            new[] { "node", "erlang", "elixir", "python", "go", "dotnet", "caddy" }.Select(RailpackProviders.PackageTitle));
        Assert.Equal("kerl", RailpackProviders.PackageTitle("kerl"));
    }

    [Fact]
    public void A_railpack_json_turns_build_with_railpack_on_unless_the_repos_dockerfile_builds_it()
    {
        using var repo = new TempRepo().With("railpack.json", """{ "deploy": { "startCommand": "./serve" } }""").With("package.json", "{}");
        Assert.True(RailpackProviders.Suggested(repo.Root, new DetectionResult { Kind = ProjectKind.NodeServer }));
        Assert.True(RailpackProviders.Suggested(repo.Root, new DetectionResult { Kind = ProjectKind.Unknown }));
        Assert.False(RailpackProviders.Suggested(repo.Root, new DetectionResult { Kind = ProjectKind.Dockerfile }));

        using var plain = new TempRepo().With("package.json", "{}");
        Assert.False(RailpackProviders.Suggested(plain.Root, new DetectionResult { Kind = ProjectKind.NodeServer }));
    }

    [Fact]
    public void A_project_gitberth_doesnt_know_gets_railpacks_card_on_port_8080()
    {
        using var repo = new TempRepo().With("start.sh", "echo hi").With("marker.txt", "railpack v1");
        var plan = Planned(Info("shell", start: "sh start.sh"));

        var r = RailpackDetection.ToDetection(plan, "", repo.Root, port: null, fallback: true);

        Assert.Equal((ProjectKind.Railpack, "Shell script with Railpack", 8080, ""), (r.Kind, r.Title, r.ContainerPort, r.BuildContext));
        Assert.True(r.CanBuild);
        // Still Gitberth's catch-all: the card is a fallback, and the app's Build stays Detect automatically.
        Assert.True(r.Unrecognized);
        Assert.Same(plan, r.RailpackPlan);
        Assert.Equal(["start.sh", "Railpack 0.43.0 plans the build: no Dockerfile needed", "Starts with `sh start.sh`", "Gets PORT=8080, the port Gitberth publishes"],
            r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Null(r.Dockerfile);
    }

    [Fact]
    public void Build_with_railpack_keeps_gitberths_port_and_ticks_each_tool()
    {
        using var repo = new TempRepo().With("web/mix.exs", "defmodule Hello.MixProject do end").With("web/railpack.json", "{}");
        var plan = Planned(Info("Elixir", start: "/app/_build/prod/rel/hello/bin/hello start",
            packages: [new("elixir", "1.18", "1.18.4", "mix.exs"), new("erlang", null, "27.3.4", null)],
            logs:
            [
                new("info", "Using the mix.exs release", null),
                new("warn", "The `railpack.json` format is still evolving and may change in future releases.", null),
                new("deprecation", "RAILPACK_OLD_SETTING is deprecated", "/config/environment-variables"),
                new("suggestion", "Pin Elixir in mix.exs", null),
            ]));

        var r = RailpackDetection.ToDetection(plan, "web", Path.Combine(repo.Root, "web"), port: 4000, fallback: false);

        Assert.Equal(("Elixir with Railpack", 4000, "web"), (r.Title, r.ContainerPort, r.BuildContext));
        Assert.False(r.Unrecognized);
        Assert.Equal(
        [
            "mix.exs", "railpack.json", "Railpack 0.43.0 plans the build: no Dockerfile needed", "Elixir 1.18.4", "Erlang 27.3.4",
            "Starts with `/app/_build/prod/rel/hello/bin/hello start`", "Gets PORT=4000, the port Gitberth publishes",
        ], r.Evidence);
        // Railpack's warnings and deprecations are the card's; its info and suggestions aren't.
        Assert.Equal(["The `railpack.json` format is still evolving and may change in future releases.", "RAILPACK_OLD_SETTING is deprecated"], r.Warnings);

        // Only a plan that worked makes a card.
        Assert.Throws<ArgumentException>(() => RailpackDetection.ToDetection(Failed(RailpackFailure.NotInstalled, RailpackText.NotInstalled), "", repo.Root, null, true));
    }

    [Fact]
    public void When_railpack_cant_build_it_either_the_unknown_card_says_why()
    {
        using var repo = new TempRepo().With("README.md", "nothing to run");
        var unknown = new ProjectAnalyzer().Analyze(repo.Root);

        var nothing = RailpackDetection.CouldntEither(unknown, Failed(RailpackFailure.NothingFound, RailpackText.CantBuild(RailpackText.FoundNothing)));

        Assert.Same(unknown, nothing);
        Assert.Equal(ProjectKind.Unknown, nothing.Kind);
        // Gitberth's own warning, then Railpack's.
        Assert.Equal(2, nothing.Warnings.Count);
        Assert.Equal("Railpack couldn't work out how to build it either: it builds from files like start.sh, mix.exs, gleam.toml, CMakeLists.txt or build.zig, and found none in the app's folder.",
            nothing.Warnings[^1]);

        // Railpack's own words when it said something else, the first line of them.
        var said = Info("node", logs: [new("info", "Detected Node", null), new("error", "failed to resolve node@99\nmise ERROR no version 99", null)]);
        Assert.Equal("Railpack couldn't work out how to build it either: failed to resolve node@99",
            RailpackDetection.CouldntEither(new DetectionResult(), Failed(RailpackFailure.Other, RailpackText.CantBuild("failed to resolve node@99"), said)).Warnings.Single());
        Assert.Equal("Railpack couldn't work out how to build it either: exit status 2",
            RailpackDetection.CouldntEither(new DetectionResult(), Failed(RailpackFailure.Other, RailpackText.CantBuild("exit status 2"), null, "Planning", "exit status 2", " ")).Warnings.Single());
        // The failure's own sentence when Railpack never got to say.
        Assert.Equal("Railpack couldn't work out how to build it either: This Gitberth image has no Railpack, so the app can't be built with it.",
            RailpackDetection.CouldntEither(new DetectionResult(), Failed(RailpackFailure.NotInstalled, RailpackText.NotInstalled)).Warnings.Single());
        // A plan that worked adds nothing.
        Assert.Empty(RailpackDetection.CouldntEither(new DetectionResult(), Planned(Info("shell"))).Warnings);
    }

    [Fact]
    public async Task Without_railpack_every_plan_fails_as_not_installed()
    {
        var none = new NoRailpack();
        var request = new RailpackPlanRequest("/tmp/repo", "", [new EnvValue("API_KEY", "x", IsSecret: true)], HasStartCommand: false, Previous: []);

        var plan = await none.PlanAsync(request, _ => throw new InvalidOperationException("No Railpack, so no output."), CancellationToken.None);

        Assert.Equal((false, RailpackFailure.NotInstalled, RailpackText.NotInstalled), (plan.Ok, plan.Failure, plan.Error));
        Assert.Null(plan.Info);
        Assert.Null(plan.PlanJson);
        Assert.Empty(plan.Output);
        Assert.Null(await none.VersionAsync(CancellationToken.None));
        Assert.Equal(new RailpackBuildKitStatus(Exists: false, Running: false, Image: null, CacheBytes: null), await none.StatusAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => none.PlanAsync(request, null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void A_failed_plan_or_build_ends_the_deploy_with_one_of_these()
    {
        Assert.Equal("Neither Gitberth nor Railpack could work out how to build this project. Add a Dockerfile to the repo, or set a custom Dockerfile in the app's settings.",
            RailpackText.NeitherCanBuild);
        // A reason is cut to its first line: Railpack's can run to a list of languages and a file tree.
        Assert.Equal("Railpack couldn't work out how to build this project: Railpack could not determine how to build the app.",
            RailpackText.CantBuild("Railpack could not determine how to build the app.\n\nThe following languages are supported:\nPhp\nGolang"));
        Assert.Equal("Railpack found a Shell script project but no command that starts it. Set one in the app's Settings › Command, or as deploy.startCommand in the repo's railpack.json.",
            RailpackText.NoStartCommand("shell"));
        Assert.StartsWith("Railpack found an Elixir project but no command that starts it.", RailpackText.NoStartCommand("Elixir"));
        Assert.StartsWith("Railpack found no command that starts this project. Set one in", RailpackText.NoStartCommand(null));
        Assert.Equal("Railpack couldn't read the repo's railpack.json: invalid character '}' looking for beginning of object key string",
            RailpackText.BadConfig("invalid character '}' looking for beginning of object key string"));
        Assert.Equal("Railpack couldn't look up the tools' versions online (lookup mise-versions.jdx.dev: no such host). The next deploy tries again.",
            RailpackText.Offline("lookup mise-versions.jdx.dev: no such host."));
        Assert.Equal("Railpack took longer than 5 minutes to plan the build, so Gitberth stopped it.", RailpackText.TimedOut);
        Assert.Equal("This Gitberth image has no Railpack, so the app can't be built with it.", RailpackText.NotInstalled);
        Assert.Equal("Gitberth couldn't start the container Railpack plans the build in: No such image: gitberth:latest",
            RailpackText.NoSandbox("No such image: gitberth:latest"));
        Assert.Equal("Gitberth couldn't start gitberth-buildkit, where Railpack builds run: it wasn't ready within 60 seconds",
            RailpackText.BuildKitFailed("it wasn't ready within 60 seconds"));
        Assert.Equal("Railpack builds run in gitberth-buildkit, which needs a privileged container, and this Docker host refused one: privileged mode is disabled",
            RailpackText.NoPrivileged("privileged mode is disabled"));
        Assert.Equal("Gitberth couldn't add its labels to the image Railpack built: no space left on device. The running container wasn't touched.",
            RailpackText.FinishFailed("no space left on device"));
        Assert.Equal("The image build failed; the log above shows why. The running container wasn't touched.", RailpackText.BuildFailed);
        Assert.Equal("Neither Gitberth nor Railpack found how to build it, so it has a starter Dockerfile that only sleeps: replace it in Settings › Source.",
            RailpackText.StarterNote);
        // A blank reason goes, with its colon.
        Assert.Equal("Railpack couldn't work out how to build this project.", RailpackText.CantBuild(" \n "));
        Assert.Equal("Railpack couldn't look up the tools' versions online. The next deploy tries again.", RailpackText.Offline(""));
        Assert.Equal("Gitberth couldn't add its labels to the image Railpack built. The running container wasn't touched.", RailpackText.FinishFailed(""));
    }

    [Fact]
    public void The_deploy_log_says_each_step_of_a_railpack_build()
    {
        Assert.Equal("Gitberth doesn't know this kind of project, so Railpack builds it", RailpackText.Fallback);
        Assert.Equal("Building with Railpack, as the app's settings say", RailpackText.Chosen);
        Assert.Equal("The repo's Dockerfile isn't used: Railpack builds from the source.", RailpackText.RepoDockerfileUnused);
        Assert.Equal("Tool versions are looked up again, since the image that ran is gone.", RailpackText.VersionsLookedUpAgain);
        Assert.Equal("Planning the build with Railpack 0.43.0", RailpackText.Planning);
        Assert.Equal("Railpack found Shell script with Railpack", RailpackText.Found(Info("shell")));
        Assert.Equal("Railpack found Elixir with Railpack: elixir 1.18.4, erlang 27.3.4",
            RailpackText.Found(Info("Elixir", packages: [new("elixir", null, "1.18.4", null), new("erlang", null, "27.3.4", null)])));
        Assert.Equal("Not passed to Railpack's build, because every build step would get it in place of its own: PATH, HOME", RailpackText.NotPassed(["PATH", "HOME"]));
        Assert.Equal("Keeping the tool versions the running image was built with: node@22.11.0", RailpackText.Keeping(["node@22.11.0"]));
        Assert.Equal("Reachable by name while building: postgres, redis", RailpackText.Reachable(["postgres", "redis"]));
        Assert.Equal("Not reachable by name while building with Railpack: postgres, redis. Railpack's builder can't join Gitberth's network yet; the app reaches them as usual once it runs.",
            RailpackText.NotReachable(["postgres", "redis"]));
        Assert.Equal("Building gitberth/shop:1a2b3c4 with Railpack", RailpackText.Building("gitberth/shop:1a2b3c4"));
        Assert.Equal("Building gitberth/shop:1a2b3c4 with Railpack without the build cache: every step runs again, and the tools' versions are looked up again",
            RailpackText.BuildingWithoutCache("gitberth/shop:1a2b3c4"));
        Assert.Equal("warning: the repo's railpack.json asks for NPM_TOKEN, which this app doesn't set: the build gets it empty.", RailpackText.MissingSecret("NPM_TOKEN"));
        Assert.Equal(("Adding Gitberth's labels to the image, and PORT=8080", "Adding Gitberth's labels to the image"), (RailpackText.Finishing(8080), RailpackText.Finishing(null)));
        Assert.Equal("The app gets PORT=8080, the port Gitberth sends visitors to.", RailpackText.GetsPort(8080));
        Assert.Equal("Downloading BuildKit (moby/buildkit v0.34.0) for Railpack builds, once", RailpackText.DownloadingBuildKit);
        Assert.Equal("Starting gitberth-buildkit, Gitberth's BuildKit for Railpack builds", RailpackText.StartingBuildKit);
        // prepare's output dimmed as a generated Dockerfile's lines are, Railpack's warnings as the log's own, buildkitd's lines tagged.
        Assert.Equal(("dim", "build: chmod +x start.sh"), (LogLine.Parse(RailpackText.Output("build: chmod +x start.sh")).Kind, LogLine.Parse(RailpackText.Output("build: chmod +x start.sh")).Text));
        Assert.Equal("warn", LogLine.Parse(RailpackText.Warning("The `railpack.json` format is still evolving\nand may change")).Kind);
        Assert.Equal("warning: The `railpack.json` format is still evolving", RailpackText.Warning("The `railpack.json` format is still evolving\nand may change"));
        Assert.Equal("  buildkit | frontend: failed to read the plan", RailpackText.BuildKitLog("frontend: failed to read the plan"));
    }

    [Fact]
    public void New_apps_card_says_who_builds_it_and_how_to_choose()
    {
        Assert.Equal("Asking Railpack…", RailpackText.Asking);
        Assert.Equal("Gitberth doesn't build this kind of project itself, so Railpack, Railway's open-source builder, makes the image. It plans the build again on every deploy.",
            RailpackText.FallbackNote);
        Assert.EndsWith("It plans the build again on every deploy.", RailpackText.ChosenNote);
        Assert.Equal("Build with Railpack", RailpackText.SwitchTitle);
        Assert.Equal("Railway's builder makes the image, instead of the Dockerfile Gitberth would write.", RailpackText.SwitchSub);
        Assert.Equal("Railway's builder makes the image, instead of the repo's Dockerfile.", RailpackText.SwitchSubRepoDockerfile);
        Assert.Equal("Railpack's plan", RailpackText.PlanTitle);
        Assert.Equal("Elixir", RailpackText.PackageTick(new RailpackPackage("elixir", null, "", null)));
    }

    [Fact]
    public void One_railpack_release_and_its_buildkit_are_pinned()
    {
        Assert.Equal("0.43.0", RailpackRelease.Version);
        Assert.StartsWith($"ghcr.io/railwayapp/railpack-frontend:v{RailpackRelease.Version}@sha256:", RailpackRelease.Frontend);
        Assert.StartsWith("moby/buildkit:v0.34.0@sha256:", RailpackRelease.BuildKitImage);
        Assert.Equal(("2026.10.14", "/usr/local/lib/railpack/mise"), (RailpackRelease.MiseVersion, RailpackRelease.MisePath));
        // Every app and preview has mount caches of its own.
        Assert.Equal("gb-12-shop", RailpackRelease.CacheKey(12, "shop"));
        Assert.Equal((8080, "gitberth-buildkit", "railpack", "buildkit"),
            (RailpackRelease.DefaultPort, RailpackRelease.BuildKitContainer, RailpackRelease.HelperKind, RailpackRelease.BuildKitHelperKind));
        Assert.Equal(("gitberth.builder", "gitberth.railpack", "gitberth.railpack.packages"), (ImageLabels.Builder, ImageLabels.RailpackVersion, ImageLabels.RailpackPackages));
    }

    [Fact]
    public void No_apps_cache_key_followed_by_a_cache_name_is_another_apps()
    {
        // Railpack names a mount cache <cache-key>-<the name railpack.json gives it>. The review's case: shop (#3), built from
        // a repo its owner doesn't control, names a cache "9-cargo_target"; shop-3 (#9) has its own cargo_target.
        Assert.NotEqual(RailpackRelease.CacheKey(3, "shop") + "-9-cargo_target", RailpackRelease.CacheKey(9, "shop-3") + "-cargo_target");
        // With the ID right after gb-, one app's key and a dash never start another's, whatever the slugs.
        (int Id, string Slug)[] apps = [(1, "shop"), (12, "shop"), (3, "shop"), (9, "shop-3"), (3, "a"), (31, "a"), (13, "1-a"), (1, "3-1-a")];
        foreach (var a in apps)
            foreach (var b in apps.Where(b => b.Id != a.Id))
                Assert.False((RailpackRelease.CacheKey(b.Id, b.Slug) + "-").StartsWith(RailpackRelease.CacheKey(a.Id, a.Slug) + "-", StringComparison.Ordinal), $"{a} and {b}");
    }

    [Fact]
    public void Railpacks_own_settings_are_always_build_time()
    {
        Assert.True(EnvRules.IsRailpackKey("RAILPACK_NODE_VERSION"));
        Assert.True(EnvRules.IsRailpackKey("RAILPACK_START_CMD"));
        Assert.False(EnvRules.IsRailpackKey("railpack_node_version"));
        Assert.False(EnvRules.IsRailpackKey("MY_RAILPACK_TOKEN"));
        Assert.False(EnvRules.IsRailpackKey("RAILPACK"));
    }

    [Fact]
    public void Gitberth_buildkit_is_said_to_stay_up_between_builds_and_the_other_helpers_to_run_for_a_moment()
    {
        static Dictionary<string, string> Helper(string kind) => new() { [HostScanHelper.Label] = kind };
        const string Moment = "a Gitberth helper: it only runs for a moment.";

        Assert.Equal(RailpackText.BuildKitHelper, HostScanHelper.What(Helper(RailpackRelease.BuildKitHelperKind)));
        Assert.Contains("10 minutes", RailpackText.BuildKitHelper);
        Assert.Equal(Moment, HostScanHelper.What(Helper(RailpackRelease.HelperKind)));
        Assert.Equal(Moment, HostScanHelper.What(Helper("ports")));
    }
}
