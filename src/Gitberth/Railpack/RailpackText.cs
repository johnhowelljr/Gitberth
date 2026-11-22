namespace Gitberth.Railpack;

/// <summary>
/// Every sentence Gitberth says about Railpack, in one place: the deploy log's lines in the order a build says them, the
/// errors a deploy, New app and create_app end with, and New app's card (its ticks, notes, banner and switch). A reason
/// or an error put into one is cut to its first line, and a blank one is left out with its colon.
/// </summary>
public static class RailpackText
{
    // ---- Which builder builds it: the deploy log's step lines ----

    /// <summary>Detect automatically, and Gitberth's analyzer found nothing it knows (DetectionResult.Unrecognized).</summary>
    public const string Fallback = "Gitberth doesn't know this kind of project, so Railpack builds it";

    /// <summary>The app's Build is Railpack.</summary>
    public const string Chosen = "Building with Railpack, as the app's settings say";

    /// <summary>The app's Build is Railpack, and the repo has a Dockerfile.</summary>
    public const string RepoDockerfileUnused = "The repo's Dockerfile isn't used: Railpack builds from the source.";

    /// <summary>A Recover build of an app whose image is gone: the label that kept its tool versions went with the image.</summary>
    public const string VersionsLookedUpAgain = "Tool versions are looked up again, since the image that ran is gone.";

    // ---- Planning ----

    public const string Planning = $"Planning the build with Railpack {RailpackRelease.Version}";

    /// <summary>One line of prepare's own output, dimmed in the log as the generated Dockerfile's lines are.</summary>
    public static string Output(string line) => "  | " + line;

    /// <summary>The app's values that go to neither prepare nor the build (RailpackPlanner.NotPassed), when it sets any.</summary>
    public static string NotPassed(IEnumerable<string> names) =>
        "Not passed to Railpack's build, because every build step would get it in place of its own: " + Join(names);

    /// <summary>"Railpack found Shell script with Railpack", or with its tools: "…: elixir 1.18.4, erlang 27.3.4".</summary>
    public static string Found(RailpackInfo info)
    {
        var found = "Railpack found " + RailpackProviders.Summary(info);
        var tools = info.Packages.Where(p => p.Name.Trim().Length > 0)
            .Select(p => p.Resolved.Trim().Length > 0 ? $"{p.Name.Trim()} {p.Resolved.Trim()}" : p.Name.Trim()).ToList();
        return tools.Count == 0 ? found : $"{found}: {Join(tools)}";
    }

    /// <summary>One of Railpack's warn or deprecation logs, as the log's other warnings are written.</summary>
    public static string Warning(string message) => "warning: " + FirstLine(message);

    /// <summary>The versions a plain redeploy keeps (--previous), from the running image's label: node@22.11.0.</summary>
    public static string Keeping(IEnumerable<string> previous) =>
        "Keeping the tool versions the running image was built with: " + Join(previous);

    // ---- gitberth-buildkit ----

    /// <summary>Its first pull: "Downloading BuildKit (moby/buildkit v0.34.0) for Railpack builds, once".</summary>
    public static string DownloadingBuildKit { get; } = $"Downloading BuildKit ({ImageWords(RailpackRelease.BuildKitImage)}) for Railpack builds, once";

    public const string StartingBuildKit = $"Starting {RailpackRelease.BuildKitContainer}, Gitberth's BuildKit for Railpack builds";

    // ---- Building ----

    /// <summary>A secret the plan asks for (the repo's railpack.json) that the app doesn't set: the build gets an empty one.</summary>
    public static string MissingSecret(string name) =>
        $"warning: the repo's railpack.json asks for {name}, which this app doesn't set: the build gets it empty.";

    /// <summary>The containers on Gitberth's network, when Railpack's build steps reach them by name.</summary>
    public static string Reachable(IEnumerable<string> names) => "Reachable by name while building: " + Join(names);

    /// <summary>The containers on Gitberth's network, when Railpack's build steps can't reach them by name.</summary>
    public static string NotReachable(IEnumerable<string> names) =>
        $"Not reachable by name while building with Railpack: {Join(names)}. Railpack's builder can't join Gitberth's network yet; the app reaches them as usual once it runs.";

    /// <summary>"Building gitberth/shop:1a2b3c4 with Railpack".</summary>
    public static string Building(string image) => $"Building {image} with Railpack";

    /// <summary>Deploy without the build cache.</summary>
    public static string BuildingWithoutCache(string image) =>
        $"Building {image} with Railpack without the build cache: every step runs again, and the tools' versions are looked up again";

    /// <summary>One line of gitberth-buildkit's own log, where the frontend's words go, after a failed build.</summary>
    public static string BuildKitLog(string line) => "  buildkit | " + line;

    /// <summary>A failed build: the same words a Dockerfile build fails with.</summary>
    public const string BuildFailed = "The image build failed; the log above shows why. The running container wasn't touched.";

    // ---- Finishing the image, and running it ----

    /// <summary>The finishing build: Gitberth's labels, and PORT when the app has a web port.</summary>
    public static string Finishing(int? port) =>
        port is int p ? $"Adding Gitberth's labels to the image, and PORT={p}" : "Adding Gitberth's labels to the image";

    /// <summary>The container's PORT, when the app's own variables don't set one.</summary>
    public static string GetsPort(int port) => $"The app gets PORT={port}, the port Gitberth sends visitors to.";

    // ---- Errors: a deploy's (dep.Error), create_app's and New app's ----

    /// <summary>Detect automatically, and Railpack found nothing to build either.</summary>
    public const string NeitherCanBuild =
        "Neither Gitberth nor Railpack could work out how to build this project. Add a Dockerfile to the repo, or set a custom Dockerfile in the app's settings.";

    public static string CantBuild(string why) => Reason("Railpack couldn't work out how to build this project", why);

    /// <summary>Why, when Railpack found nothing it builds from (RailpackFailure.NothingFound).</summary>
    public const string FoundNothing =
        "it builds from files like start.sh, mix.exs, gleam.toml, CMakeLists.txt or build.zig, and found none in the app's folder.";

    /// <param name="provider">Railpack's provider name (shell, node…), as RailpackInfo.Provider has it.</param>
    public static string NoStartCommand(string? provider)
    {
        const string fix = "Set one in the app's Settings › Command, or as deploy.startCommand in the repo's railpack.json.";
        if (string.IsNullOrWhiteSpace(provider)) return $"Railpack found no command that starts this project. {fix}";
        var title = RailpackProviders.Title(provider);
        return $"Railpack found {Article(title)} {title} project but no command that starts it. {fix}";
    }

    public static string BadConfig(string error) => Reason("Railpack couldn't read the repo's railpack.json", error);

    public static string Offline(string error) =>
        (FirstLine(error).TrimEnd('.') is { Length: > 0 } why
            ? $"Railpack couldn't look up the tools' versions online ({why})."
            : "Railpack couldn't look up the tools' versions online.")
        + " The next deploy tries again.";

    public const string TimedOut = "Railpack took longer than 5 minutes to plan the build, so Gitberth stopped it.";

    public const string NotInstalled = "This Gitberth image has no Railpack, so the app can't be built with it.";

    public static string NoSandbox(string why) => Reason("Gitberth couldn't start the container Railpack plans the build in", why);

    public static string BuildKitFailed(string why) =>
        Reason($"Gitberth couldn't start {RailpackRelease.BuildKitContainer}, where Railpack builds run", why);

    public static string NoPrivileged(string why) =>
        Reason($"Railpack builds run in {RailpackRelease.BuildKitContainer}, which needs a privileged container, and this Docker host refused one", why);

    public static string FinishFailed(string why) =>
        (FirstLine(why) is { Length: > 0 } reason
            ? $"Gitberth couldn't add its labels to the image Railpack built: {Sentence(reason)}"
            : "Gitberth couldn't add its labels to the image Railpack built.")
        + " The running container wasn't touched.";

    /// <summary>create_app's message when neither could build it, and the app got the Starter Dockerfile.</summary>
    public const string StarterNote =
        "Neither Gitberth nor Railpack found how to build it, so it has a starter Dockerfile that only sleeps: replace it in Settings › Source.";

    // ---- New app: the card "How it will run" ----

    /// <summary>The analyzing state, while Railpack plans it.</summary>
    public const string Asking = "Asking Railpack…";

    /// <summary>A tick: Railpack plans the build.</summary>
    public const string PlansTheBuild = $"Railpack {RailpackRelease.Version} plans the build: no Dockerfile needed";

    /// <summary>A tick per tool: "Elixir 1.18.4".</summary>
    public static string PackageTick(RailpackPackage package) =>
        $"{RailpackProviders.PackageTitle(package.Name)} {package.Resolved.Trim()}".TrimEnd();

    /// <summary>A tick: "Starts with `sh start.sh`".</summary>
    public static string StartsWith(string command) => $"Starts with `{command.Trim()}`";

    /// <summary>A tick: "Gets PORT=8080, the port Gitberth publishes".</summary>
    public static string PortTick(int port) => $"Gets PORT={port}, the port Gitberth publishes";

    /// <summary>The help line under the ticks when Gitberth didn't know the project (Detect automatically).</summary>
    public const string FallbackNote =
        "Gitberth doesn't build this kind of project itself, so Railpack, Railway's open-source builder, makes the image. It plans the build again on every deploy.";

    /// <summary>The help line under the ticks when Build with Railpack is on.</summary>
    public const string ChosenNote =
        "Build with Railpack is on, so Railpack, Railway's open-source builder, makes the image from the source. It plans the build again on every deploy.";

    /// <summary>The banner on the Unknown card when Railpack couldn't plan it either (RailpackDetection.CouldntEither).</summary>
    public static string CouldntEither(string why) => Reason("Railpack couldn't work out how to build it either", why);

    /// <summary>The switch's title.</summary>
    public const string SwitchTitle = "Build with Railpack";

    /// <summary>The switch's line, for a project Gitberth writes the Dockerfile of.</summary>
    public const string SwitchSub = "Railway's builder makes the image, instead of the Dockerfile Gitberth would write.";

    /// <summary>The switch's line, for a repo with its own Dockerfile (ProjectKind.Dockerfile).</summary>
    public const string SwitchSubRepoDockerfile = "Railway's builder makes the image, instead of the repo's Dockerfile.";

    /// <summary>The title of the block with prepare's output.</summary>
    public const string PlanTitle = "Railpack's plan";

    // ---- Helpers ----

    private static string Join(IEnumerable<string> items) => string.Join(", ", items);

    // "Head: why", or "Head." when there's no why.
    private static string Reason(string head, string why) => FirstLine(why) is { Length: > 0 } line ? $"{head}: {line}" : head + ".";

    // The first line with something on it, trimmed: Railpack's errors can run to a list of languages and a file tree.
    private static string FirstLine(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    // A reason that more words follow: it ends as a sentence does.
    private static string Sentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    // "an Elixir project", "a Shell script project".
    private static string Article(string word) => word.Length > 0 && "AEIOaeio".Contains(word[0]) ? "an" : "a";

    // "moby/buildkit:v0.34.0@sha256:…" → "moby/buildkit v0.34.0".
    private static string ImageWords(string image)
    {
        var named = image.Split('@')[0];
        var colon = named.LastIndexOf(':');
        return colon > named.LastIndexOf('/') ? $"{named[..colon]} {named[(colon + 1)..]}" : named;
    }

    // ---- Added with the backend: the port a Railpack image's app opens (plan D4) ----

    /// <summary>The one other port the app listens on: its web port moves there, and the swap runs once more.</summary>
    public static string PortMoves(int found, int web) => $"It listens on port {found}, not {web}, so its web port moves to {found}.";

    /// <summary>
    /// What the app listens on instead of its web port, when the web port can't just move: several other ports (or one,
    /// once it moved already), ports only inside its container (127.0.0.1), or none. It follows the timed-out wait's words.
    /// </summary>
    /// <param name="ports">The other ports it listens on, for anyone to reach.</param>
    /// <param name="inside">The ports it listens on only inside its container.</param>
    public static string ListensElsewhere(IReadOnlyList<int> ports, IReadOnlyList<int> inside, int web) =>
        ports.Count > 0 ? $"It listens on {PortWords(ports)}, not {web}: set its web port on the Network tab, or have it listen on $PORT."
        : inside.Count > 0 ? $"It listens on {PortWords(inside)} only inside its container (127.0.0.1): have it listen on 0.0.0.0, on $PORT."
        : "It doesn't listen on any other port either: have it listen on $PORT.";

    // "4000", "4000 and 9000", "3000, 4000 and 9000".
    private static string PortWords(IReadOnlyList<int> ports) =>
        ports.Count == 1 ? ports[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
        : string.Join(", ", ports.SkipLast(1).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + " and " + ports[^1].ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- Added at the merge ----

    /// <summary>
    /// What gitberth-buildkit is, after "This is" or "&lt;name&gt; is", where a page says why it isn't an app: unlike the other
    /// helpers it stays up between builds (RailpackBuildKit.IdleStop).
    /// </summary>
    public const string BuildKitHelper =
        "Gitberth's BuildKit for Railpack builds: Gitberth starts it for a build and stops it once no build has used it for 10 minutes.";

    // ---- Added with the security review's fixes ----

    /// <summary>
    /// A save refused (the Environment tab, its paste, the API, MCP, New app): a RAILPACK_ value that's marked secret, or
    /// holds one (RailpackSettings.IsSecret).
    /// </summary>
    public static string SecretSetting(string name) =>
        $"{name} can't be secret: Railpack writes its settings into the build plan and the image, so keep the secret in a variable of its own and write $NAME in the command.";

    /// <summary>
    /// The deploy log, when a RAILPACK_ value is secret anyway (a GitLab masked variable, a row saved before the rule): it
    /// reaches neither the plan nor the build.
    /// </summary>
    public static string SecretSettingsLeftOut(IReadOnlyList<string> names) =>
        $"warning: {Join(names)} {(names.Count == 1 ? "is" : "are")} secret, so Railpack doesn't get {(names.Count == 1 ? "it" : "them")}: Railpack writes its "
        + $"settings into the build plan and the image. Make {(names.Count == 1 ? "it" : "them")} plain, or keep the secret in a variable of its own and write $NAME in the command.";

    /// <summary>
    /// A deploy that fails before the build: the plan asks for a secret (from the repo's railpack.json) whose name can't be a
    /// variable's, cut to 60 characters.
    /// </summary>
    public static string BadSecretName(string name) =>
        $"The repo's railpack.json asks for a secret called \"{(name.Length > 60 ? name[..60] + "…" : name)}\", which isn't a variable name "
        + "(letters, digits and _, not starting with a digit), so nothing was built.";
}
