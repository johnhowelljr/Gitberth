using System.Collections.Concurrent;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Sources;

/// <summary>What creating an app needs from the other services; tests stand in for them.</summary>
internal interface ICreateSteps
{
    Task<NetworkName> ChooseSlugAsync(string name, CancellationToken ct);
    /// <summary>The host's LAN address, as every page shows it.</summary>
    Task<string> LanHostAsync(CancellationToken ct);
    Task<IReadOnlyList<HostPortUse>> HostPortsAsync(CancellationToken ct);
    Task<int> SuggestAsync(int containerPort, string protocol, IEnumerable<int> alsoTaken, CancellationToken ct);
    /// <summary>The networks that exist.</summary>
    Task<IReadOnlySet<string>> NetworksAsync(CancellationToken ct);
    Task<AppDefinition> AddAsync(NewAppRow row, CancellationToken ct);
    Task SyncAsync(int appId, CancellationToken ct);
    Task<PortChangePlan> PlanCreateAsync(NewContainerRequest request, CancellationToken ct);
    PortChangeJob Start(PortChangePlan plan);
    Task RememberAsync(string container, string network, string? alias, string? ipv4, CancellationToken ct);
    Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct);
    /// <summary>A failed create's row goes (AppCatalog.DiscardAsync).</summary>
    Task DiscardAsync(int appId, CancellationToken ct);
    /// <summary>Whether Docker has a container by that name.</summary>
    Task<bool> ExistsAsync(string container, CancellationToken ct);
    /// <summary>Settings › Updates › New apps from an image.</summary>
    Task<UpdatePolicy> DefaultPolicyAsync(CancellationToken ct);
}

/// <summary>
/// Creates an app from a registry image, a local image, by hand or from a pasted docker run or Compose service (3.2 to
/// 3.6), so it comes out exactly like one Gitberth deploys from GitHub (3.1): container gb-&lt;slug&gt;, labels
/// gitberth.managed, gitberth.app and gitberth.source, on the shared network with its alias, restart unless-stopped, the
/// daemon's log driver (with rotation for json-file), every exposed port on the LAN on a suggested host port, and every
/// image volume named so its data outlives the container. The sequence (3.8): plan, then in the background pull when
/// needed, write the row, create through the change engine, then sync and note it in Activity. A create that's refused, or
/// whose first start fails its checks, leaves nothing in Docker but its volumes and removes its row (freeing its host
/// ports): the result carries why and the last output, and the wizard keeps the draft to try again under the same name.
/// </summary>
public sealed class AppCreator
{
    private readonly ICreateSteps _steps;
    private readonly ImageService _images;
    private readonly GitberthOptions _options;
    private readonly ILogger<AppCreator> _logger;
    private readonly ConcurrentDictionary<string, CreateJob> _jobs = new(StringComparer.Ordinal);

    public AppCreator(AppService apps, AppCatalog catalog, ImageService images, PortChanger changer, NetworkService network,
        HostPortMap hostPorts, HostScanner scanner, ActivityLog activity, SettingsService settings, GitberthOptions options, ILogger<AppCreator> logger)
        : this(new LiveSteps(apps, catalog, changer, network, hostPorts, activity, settings), images, options, logger)
    {
    }

    internal AppCreator(ICreateSteps steps, ImageService images, GitberthOptions options, ILogger<AppCreator> logger)
    {
        _steps = steps;
        _images = images;
        _options = options;
        _logger = logger;
    }

    /// <summary>The real-engine test's hook: the container a slug gets, in place of gb-&lt;slug&gt;, so it can make gbtest-* ones.</summary>
    internal Func<string, string>? ContainerFor { get; init; }

    /// <summary>
    /// Configure's prefill from an image (3.1): ports LAN with suggested host ports, named volumes, hints, and the update
    /// policy Settings gives new apps. An image the host doesn't have yet is read from its registry (its config, nothing
    /// pulled), so its ports, volumes and check are filled in as for one that's here; when that can't be done in a few
    /// seconds, they're filled in once it's pulled.
    /// </summary>
    public async Task<NewAppDraft> DraftFromImageAsync(string reference, AppSource source, int? registryId = null, CancellationToken ct = default)
    {
        var image = reference.Trim();
        var issues = new List<ParseIssue>();
        var facts = await _images.FactsAsync(image, ct);
        var parsed = ImageReference.Parse(image);
        if (facts is null && parsed is not null) facts = await _images.RemoteFactsAsync(image, registryId, ct);
        var name = parsed?.ShortName ?? facts?.Tags.Select(t => ImageReference.Parse(t)?.ShortName).FirstOrDefault(n => n is not null) ?? "app";
        var slug = await SlugAsync(name, ct);
        if (facts is null)
            issues.Add(new ParseIssue(ParseSeverity.Note, $"{parsed?.Familiar ?? image} isn't on this host yet: once it's pulled, its ports and volumes are filled in.", image));
        else if (parsed is null)
            issues.Add(new ParseIssue(ParseSeverity.Note, $"It runs the image {ImageService.Short(facts.Id)} by its ID: there's no tag to follow for updates.", image));

        var settings = new ContainerSettings { Image = image, Env = facts?.Env ?? [], Logging = await LoggingAsync(ct) };
        settings = settings with
        {
            Ports = await SuggestedAsync(facts?.ExposedPorts.Select(p => new PortRule(p.Port, p.Protocol, PortAccess.Lan)) ?? [], [], ct),
            Mounts = ImageVolumes(facts, slug, []),
        };
        var (env, hints) = ImageHints.For(image, settings.Env);
        issues.AddRange(hints);
        settings = settings with { Env = env };
        return new NewAppDraft
        {
            Source = source,
            Name = name,
            Settings = settings,
            Options = new AppOptions
            {
                Name = name,
                WebPort = WebPortOf(settings.Ports, image),
                TrackedImage = image,
                RegistryId = registryId,
                SecretKeys = hints.Select(h => h.Fragment!).ToList(),
                Updates = await NewPolicyAsync(image, ct),
            },
            RegistryId = registryId,
            Issues = issues,
            Slug = slug,
            ImageRead = facts is not null,
            Facts = facts,
        };
    }

    /// <summary>Configure's prefill from a pasted command or service: host ports suggested where none were given.</summary>
    public async Task<NewAppDraft> DraftFromParsedAsync(ParsedApp parsed, CancellationToken ct = default)
    {
        var issues = parsed.Issues.ToList();
        var image = parsed.Settings.Image;
        var facts = image.Length > 0 ? await _images.FactsAsync(image, ct) : null;
        // An image the host doesn't have yet is read from its registry, as for the registry source: -P's ports, the image's
        // volumes and variables are filled in before the pull.
        if (facts is null && ImageReference.Parse(image) is not null) facts = await _images.RemoteFactsAsync(image, null, ct);
        var name = parsed.Name ?? ImageReference.Parse(image)?.ShortName ?? "app";
        var slug = await SlugAsync(name, ct);
        var settings = parsed.Settings;

        // The parser named unnamed volumes after its own guess of the slug.
        settings = settings with { Mounts = Renamed(settings.Mounts, AppService.Slugify(name), slug) };

        var ports = settings.Ports.ToList();
        if (parsed.PublishAll)
        {
            if (facts is null)
                issues.Add(new ParseIssue(ParseSeverity.Note, "-P publishes every port the image exposes: they're listed once it's pulled.", "-P"));
            else
            {
                var exposed = facts.ExposedPorts.Where(p => !ports.Any(r => r.ContainerPort == p.Port && r.Protocol == p.Protocol && r.Published)).ToList();
                ports.RemoveAll(r => r.Access == PortAccess.Internal && exposed.Any(p => p.Port == r.ContainerPort && p.Protocol == r.Protocol));
                ports.AddRange(exposed.Select(p => new PortRule(p.Port, p.Protocol, PortAccess.Lan)));
                if (exposed.Count > 0)
                    issues.Add(new ParseIssue(ParseSeverity.Note, $"-P publishes every port the image exposes: {string.Join(", ", exposed.Select(p => ContainerPorts.Key(p.Port, p.Protocol)))}, each on a suggested host port.", "-P"));
            }
        }
        var given = ports.Where(p => p.HostPort is not null).Select(p => p.HostPort!.Value).ToList();
        settings = settings with { Ports = await SuggestedAsync(ports, given, ct) };
        // Every image VOLUME the paste doesn't mount gets a named volume, so its data outlives the container (3.1).
        settings = settings with { Mounts = ImageVolumes(facts, slug, settings.Mounts) };

        // The image's own variables, for "From the image"; the paste's own win.
        if (facts is not null)
            settings = settings with { Env = [.. facts.Env.Where(e => settings.Env.All(o => o.Key != e.Key)), .. settings.Env] };
        if (settings.Logging is { Driver: "", Options: null })
            settings = settings with { Logging = await LoggingAsync(ct) };
        var (env, hints) = image.Length > 0 ? ImageHints.For(image, settings.Env) : (settings.Env, []);
        issues.AddRange(hints);
        settings = settings with { Env = env };

        return new NewAppDraft
        {
            Source = AppSource.Manual,
            Name = name,
            Settings = settings,
            ExtraNetworks = parsed.Networks,
            LanAddress = parsed.LanAddress,
            Options = new AppOptions
            {
                Name = name,
                WebPort = WebPortOf(settings.Ports.Where(p => p.Published).ToList(), image),
                TrackedImage = image.Length > 0 ? image : null,
                SecretKeys = env.Where(e => e.Secret && !e.FromImage).Select(e => e.Key).ToList(),
                Updates = await NewPolicyAsync(image, ct),
            },
            Platform = parsed.Platform,
            PullFirst = parsed.PullFirst,
            Issues = issues,
            Slug = slug,
            ImageRead = facts is not null,
            Facts = facts,
        };
    }

    /// <summary>
    /// The update policy a new app gets: Settings › Updates › New apps from an image (Tell me when it can't be read). A
    /// database is never updated by itself, so Automatically only tells for one; an image pinned to a digest or an ID gets
    /// When I ask when it's created, since there's nothing to follow.
    /// </summary>
    private async Task<UpdatePolicy> NewPolicyAsync(string image, CancellationToken ct)
    {
        UpdatePolicy policy;
        try
        {
            policy = await _steps.DefaultPolicyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "The update policy for new apps couldn't be read");
            policy = UpdatePolicy.Notify;
        }
        return policy == UpdatePolicy.Auto && ContainerService.IsDatabaseImage(image) ? UpdatePolicy.Notify : policy;
    }

    /// <summary>
    /// What creating the draft would do, changing nothing: its name and container, the settings as the engine gets them
    /// (Gitberth's labels and defaults on), whether the image needs pulling and the room for it, the docker run it amounts
    /// to (secrets masked), and every problem: a name, a port or a setting that can't be, or a parse Error.
    /// </summary>
    public async Task<CreatePlan> PlanAsync(NewAppDraft draft, CancellationToken ct = default)
    {
        var problems = new List<SettingProblem>();
        var notes = new List<string>();
        problems.AddRange(draft.Issues.Where(i => i.Severity == ParseSeverity.Error).Select(i => new SettingProblem(null, i.Text)));
        if (AppService.NameProblem(draft.Name) is { } nameProblem) problems.Add(new SettingProblem(null, nameProblem));

        NetworkName chosen;
        try
        {
            chosen = await _steps.ChooseSlugAsync(draft.Name, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or EngineException or DockerApiException or HttpRequestException or TimeoutException)
        {
            problems.Add(new SettingProblem(null, ex.Message));
            chosen = new NetworkName(AppService.Slugify(draft.Name) is { Length: > 0 } s ? s : "app", null);
        }
        var slug = chosen.Slug;
        if (_jobs.TryGetValue(slug, out var running) && !running.Completion.IsCompleted)
            problems.Add(new SettingProblem(null, $"{AppDefinition.ContainerNameFor(slug)} is being created already."));
        var container = ContainerFor?.Invoke(slug) ?? AppDefinition.ContainerNameFor(slug);
        var nameTaken = chosen.Taken is { } taken ? $"{taken}: it's called {slug}" : null;

        var settings = Final(draft, slug);
        var networks = NetworksOf(draft, settings, slug);
        var secretKeys = SecretKeysOf(draft, settings);

        // The image: on the host already, or pulled first.
        var needsPull = false;
        if (settings.Image.Length == 0) problems.Add(new SettingProblem(SettingField.Image, "Give the image it runs, such as postgres:18."));
        else
        {
            var reference = ImageReference.Parse(settings.Image);
            var local = await LocalAsync(settings.Image, ct);
            needsPull = local is null || draft.PullFirst && reference is not null;
            if (local is null && reference is null)
                problems.Add(new SettingProblem(SettingField.Image, ImageReference.IsImageId(settings.Image) || settings.Image.StartsWith("sha256:", StringComparison.Ordinal)
                    ? $"The image {ImageService.Short(settings.Image)} isn't on this host."
                    : ImageService.NotAReference(settings.Image)));
        }
        var disk = _images.Disk();
        if (needsPull && disk.Problem is { } room) notes.Add(room);

        try
        {
            var host = await _images.HostAsync(ct);
            var existing = await _steps.NetworksAsync(ct);
            problems.AddRange(SettingsRules.Check(settings, new SettingsContext(host.Cpus, host.MemoryBytes, existing, GitApp: false)));
            foreach (var join in networks.Skip(1).Where(n => !existing.Contains(n.Name)))
                problems.Add(new SettingProblem(SettingField.NetworkMode, join.Name == LanAddresses.Network
                    ? "Own LAN addresses are off: turn them on in Settings › Own LAN addresses, or take the LAN address out."
                    : $"There's no network called {join.Name}: create it in Configure, or take it out."));
            var rows = settings.Ports.Select(p => new PortRow
            {
                ContainerPort = p.ContainerPort, Protocol = p.Protocol, Access = p.Access, HostPort = p.HostPort, HostIp = p.HostIp,
                IsWeb = p.Protocol == "tcp" && p.ContainerPort == draft.Options.WebPort,
            }).ToList();
            var uses = await _steps.HostPortsAsync(ct);
            problems.AddRange(PortRules.Check(rows, new PortContext([], false, uses, null)).DistinctBy(p => p.Message).Select(p => new SettingProblem(SettingField.Ports, p.Message)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or EngineException or DockerApiException or HttpRequestException or TimeoutException)
        {
            problems.Add(new SettingProblem(null, $"Docker couldn't be read to check the settings: {ex.Message}"));
        }
        if (settings.NetworkMode.Length > 0 && settings.Ports.Any(p => p.Published))
            problems.Add(new SettingProblem(SettingField.Ports, settings.NetworkMode == "host"
                ? "On the host's network it has no published ports: it listens on the host's own ports. Take the ports out, or put it on a network."
                : $"With the network mode {settings.NetworkMode} it has no ports of its own: take the ports out."));

        var lanHost = await LanHostAsync(ct);
        return new CreatePlan(draft, slug, container, nameTaken, ContainerSettingsJson.DockerRun(container, settings, networks, secretKeys), needsPull, disk,
            problems, notes)
        {
            Consequence = Consequence(container, slug, settings, draft.Options.WebPort, lanHost, needsPull),
            Settings = settings,
            Networks = networks,
        };
    }

    /// <summary>
    /// Creates the app in the background (closing the page doesn't stop it): the pull when needed, the row, then the
    /// engine's create, start and checks. A create running for the slug already is returned as it is.
    /// </summary>
    public CreateJob Start(CreatePlan plan)
    {
        if (_jobs.TryGetValue(plan.Slug, out var running) && !running.Completion.IsCompleted) return running;
        var job = new CreateJob(plan.Slug);
        job.Completion = Task.Run(async () =>
        {
            try
            {
                return await RunAsync(job, plan);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Creating {Container} failed", plan.Container);
                job.Enter("Failed");
                return new CreateResult(false, $"Creating {plan.Container} failed: {ex.Message}", null, null, null);
            }
        });
        _jobs[plan.Slug] = job;
        return job;
    }

    /// <summary>The create of the slug running now, or the last one since Gitberth started; null for none.</summary>
    public CreateJob? JobFor(string slug) => _jobs.TryGetValue(slug, out var job) ? job : null;

    private async Task<CreateResult> RunAsync(CreateJob job, CreatePlan plan)
    {
        var draft = plan.Draft;
        var settings = plan.Settings;
        if (!plan.CanCreate)
            return Failed(job, plan.Problems.FirstOrDefault()?.Message ?? plan.Disk.Problem ?? "It can't be created as it is.", null, null);

        // 1. The image, when the host doesn't have it.
        var webPort = draft.Options.WebPort;
        if (plan.NeedsPull)
        {
            var pull = _images.StartPull(settings.Image, draft.Platform, draft.RegistryId);
            job.Pulling(pull);
            var pulled = await pull.Completion;
            if (!pulled.Ok) return Failed(job, pulled.Message, null, null);
            // The image can say now what it couldn't when the draft was made: its ports and its volumes (3.1).
            if (!draft.ImageRead) (settings, webPort) = await FromPulledAsync(draft, settings, plan.Slug);
        }

        // 2. The row, before anything is made: a Gitberth restart from here on finds the app and creates it from its snapshot.
        job.Enter("Creating");
        var secretKeys = SecretKeysOf(draft, settings);
        var row = await _steps.AddAsync(new NewAppRow(draft.Name.Trim(), plan.Slug, draft.Source, settings.Image, draft.RegistryId,
            new AppSnapshot(settings, plan.Networks),
            draft.Options with
            {
                Name = draft.Name.Trim(),
                TrackedImage = settings.Image,
                RegistryId = draft.RegistryId,
                SecretKeys = secretKeys.ToList(),
                WebPort = webPort,
                Updates = ImageReference.Parse(settings.Image) is { Digest: null } ? draft.Options.Updates : UpdatePolicy.Off,
            }, plan.Container), CancellationToken.None);

        // 3. The container, with every network and its alias in the create body, through the change engine's checks.
        var request = new NewContainerRequest
        {
            Name = plan.Container,
            Settings = settings,
            Networks = plan.Networks,
            Start = true,
            HealthPath = draft.Options.HealthCheckPath,
            WebPort = webPort,
            SecretKeys = secretKeys,
            UseExistingData = draft.UseExistingData,
        };
        var enginePlan = await _steps.PlanCreateAsync(request, CancellationToken.None);
        if (enginePlan.Blocked is not null || !enginePlan.CanApply)
        {
            // Refused: nothing was made, so its row goes too (with its host ports), and a retry gets the same name.
            var why = enginePlan.Blocked ?? enginePlan.Verdict.Reason ?? "the engine refused it";
            await DiscardAsync(row.Id, plan.Container);
            return Failed(job, $"{plan.Container} wasn't created: {why}", null, null);
        }
        var engine = _steps.Start(enginePlan);
        job.Running(engine);
        engine.Changed += () =>
        {
            if (engine.Current?.Text is { } step && (step.Contains("check", StringComparison.OrdinalIgnoreCase) || step.Contains("answer", StringComparison.OrdinalIgnoreCase)
                || step.Contains("stays up", StringComparison.OrdinalIgnoreCase)))
                job.Enter("Checking");
        };
        var result = await engine.Completion;
        var familiar = ImageReference.Parse(settings.Image)?.Familiar ?? ImageService.Short(settings.Image);

        if (!result.Ok)
        {
            // 5. Its first start failed: the engine removed its container, and its row goes too (with its host ports), so
            // nothing is left to create again by itself and a retry gets the same name. Only a container Docker wouldn't
            // remove keeps its row, so the app is there to act on.
            if (await StillThereAsync(plan.Container))
            {
                await NoteAsync(plan.Container, row.Id, ActivityKind.Created, $"Its first start failed: {result.Message}", ActivityTone.Warning);
                return Failed(job, result.Message, row.Id, result.LastOutput, plan.Container);
            }
            await DiscardAsync(row.Id, plan.Container);
            return Failed(job, result.Message, null, result.LastOutput);
        }

        // 4. Done: Gitberth remembers its networks, its row follows the container, and its Activity says where it came from.
        foreach (var join in plan.Networks.Skip(1))
        {
            try
            {
                await _steps.RememberAsync(plan.Container, join.Name, join.Aliases.FirstOrDefault(), join.IPv4Address, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't remember {Container} on {Network}", plan.Container, join.Name);
            }
        }
        try
        {
            await _steps.SyncAsync(row.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't bring {Container}'s row in line after creating it", plan.Container);
        }
        await NoteAsync(plan.Container, row.Id, ActivityKind.Created, $"Created from {familiar}", ActivityTone.Good);
        job.Enter("Done");
        var message = $"Created {plan.Container} from {familiar}." + (result.Message.Length > 0 ? " " + result.Message : "");
        return new CreateResult(true, message, plan.Container, row.Id, null);
    }

    // A failed create's row goes; one that can't be removed is only logged (the app then shows as not created).
    private async Task DiscardAsync(int appId, string container)
    {
        try
        {
            await _steps.DiscardAsync(appId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't remove the record of {Container}, whose create failed", container);
        }
    }

    // Whether the failed create left a container behind (Docker didn't remove it); when Docker doesn't say, it may have.
    private async Task<bool> StillThereAsync(string container)
    {
        try
        {
            return await _steps.ExistsAsync(container, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Couldn't ask Docker whether {Container} is left", container);
            return true;
        }
    }

    /// <summary>
    /// After the pull, for a draft made before the host had the image: what the image says fills in what the owner couldn't
    /// see. A registry or local image's exposed ports go on the LAN on suggested host ports (Configure listed none), and every
    /// image VOLUME that isn't mounted gets a named volume. A paste or a form keeps its own ports: only its volumes are added.
    /// </summary>
    private async Task<(ContainerSettings Settings, int? WebPort)> FromPulledAsync(NewAppDraft draft, ContainerSettings settings, string slug)
    {
        ImageFacts? facts;
        try
        {
            facts = await _images.FactsAsync(settings.Image, CancellationToken.None);
        }
        catch (Exception ex) when (ex is EngineException or DockerApiException or HttpRequestException or TimeoutException)
        {
            _logger.LogInformation(ex, "Couldn't read {Image} after its pull", settings.Image);
            return (settings, draft.Options.WebPort);
        }
        if (facts is null) return (settings, draft.Options.WebPort);
        var webPort = draft.Options.WebPort;
        if (draft.Source is AppSource.Registry or AppSource.Local && settings.NetworkMode.Length == 0)
        {
            var exposed = facts.ExposedPorts.Where(p => !settings.Ports.Any(r => r.ContainerPort == p.Port && r.Protocol == p.Protocol))
                .Select(p => new PortRule(p.Port, p.Protocol, PortAccess.Lan)).ToList();
            var given = settings.Ports.Where(p => p.HostPort is not null).Select(p => p.HostPort!.Value).ToList();
            var ports = settings.Ports.Concat(await SuggestedAsync(exposed, given, CancellationToken.None)).ToList();
            settings = settings with { Ports = ports };
            webPort ??= WebPortOf(ports, settings.Image);
        }
        return (settings with { Mounts = ImageVolumes(facts, slug, settings.Mounts) }, webPort);
    }

    private static CreateResult Failed(CreateJob job, string message, int? appId, string? lastOutput, string? key = null)
    {
        job.Enter("Failed");
        return new CreateResult(false, message, key, appId, lastOutput);
    }

    private async Task NoteAsync(string container, int appId, ActivityKind kind, string text, ActivityTone tone)
    {
        try
        {
            await _steps.ActivityAsync(container, appId, kind, text, tone, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't write {Container}'s Activity", container);
        }
    }

    /// <summary>The settings as the engine gets them: Gitberth's labels, the container's own variables, volumes named after the final slug.</summary>
    private static ContainerSettings Final(NewAppDraft draft, string slug)
    {
        var s = draft.Settings;
        var labels = s.Labels.Where(l => !l.Key.StartsWith("gitberth.", StringComparison.Ordinal)).ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        labels["gitberth.managed"] = "true";
        labels["gitberth.app"] = slug;
        labels["gitberth.source"] = draft.Source.ToString().ToLowerInvariant();
        return s with
        {
            // The image supplies its own; writing them in would pin them across an update.
            Env = s.Env.Where(e => !e.FromImage).ToList(),
            Labels = labels,
            Mounts = draft.Slug is { } drafted ? Renamed(s.Mounts, drafted, slug) : s.Mounts,
            Image = s.Image.Trim(),
        };
    }

    /// <summary>The networks it joins: the shared one with its alias first, then the others at a low gateway priority, then its own LAN address.</summary>
    private List<NetworkJoin> NetworksOf(NewAppDraft draft, ContainerSettings settings, string slug)
    {
        if (settings.NetworkMode.Length > 0) return [];
        var sharedAliases = new List<string> { slug };
        var extras = new List<NetworkJoin>();
        foreach (var join in draft.ExtraNetworks)
        {
            if (join.Name.Length == 0 || join.Name == _options.Network) sharedAliases.AddRange(join.Aliases.Where(a => !sharedAliases.Contains(a)));
            else if (join.Name != LanAddresses.Network && extras.All(e => e.Name != join.Name)) extras.Add(join with { LowPriority = true });
        }
        var networks = new List<NetworkJoin> { new(_options.Network, sharedAliases) };
        networks.AddRange(extras);
        if (draft.LanAddress is { Length: > 0 } address) networks.Add(new NetworkJoin(LanAddresses.Network, [], address.Trim(), LowPriority: true));
        return networks;
    }

    private static IReadOnlyCollection<string> SecretKeysOf(NewAppDraft draft, ContainerSettings settings) =>
        draft.Options.SecretKeys.Concat(settings.Env.Where(e => e.Secret).Select(e => e.Key)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>"Creates gb-uptime-kuma and starts it. Reached at 10.1.1.80:3001."</summary>
    private static string Consequence(string container, string slug, ContainerSettings settings, int? webPort, string lanHost, bool needsPull)
    {
        var first = needsPull ? $"Pulls {settings.Image}, then creates {container} and starts it." : $"Creates {container} and starts it.";
        if (settings.NetworkMode == "host") return first + " It runs on the host's network.";
        if (settings.NetworkMode.Length > 0) return first;
        var web = settings.Ports.FirstOrDefault(p => p.ContainerPort == webPort && p.Protocol == "tcp" && p.Published && p.HostPort is not null)
                  ?? settings.Ports.FirstOrDefault(p => p.Access == PortAccess.Lan && p.HostPort is not null);
        return web switch
        {
            { Access: PortAccess.Lan } => $"{first} Reached at {ContainerPorts.Address(lanHost, web.HostPort!.Value)}.",
            { Access: PortAccess.HostOnly } => $"{first} Reached from the host at {ContainerPorts.Address("127.0.0.1", web.HostPort!.Value)}.",
            { Access: PortAccess.Custom } => $"{first} Reached at {ContainerPorts.Address(web.HostIp ?? lanHost, web.HostPort!.Value)}.",
            _ => $"{first} Containers on Gitberth's network reach it as {slug}.",
        };
    }

    // Published ports without a host port get one: their own number when free, else the next free one above it.
    private async Task<List<PortRule>> SuggestedAsync(IEnumerable<PortRule> ports, IEnumerable<int> given, CancellationToken ct)
    {
        var taken = given.ToList();
        var result = new List<PortRule>();
        foreach (var port in ports)
        {
            if (!port.Published || port.HostPort is not null)
            {
                result.Add(port);
                continue;
            }
            int suggested;
            try
            {
                suggested = await _steps.SuggestAsync(port.ContainerPort, port.Protocol, taken, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or EngineException or DockerApiException or HttpRequestException or TimeoutException)
            {
                _logger.LogInformation(ex, "No host port could be suggested for {Port}", port.Key);
                suggested = port.ContainerPort;
            }
            taken.Add(suggested);
            result.Add(port with { HostPort = suggested });
        }
        return result;
    }

    /// <summary>Each image VOLUME as a named volume gb-&lt;slug&gt;-&lt;last path segment&gt;, never anonymous; a name used twice gets -2.</summary>
    private static List<MountSetting> ImageVolumes(ImageFacts? facts, string slug, IReadOnlyList<MountSetting> already)
    {
        var mounts = already.ToList();
        foreach (var path in facts?.Volumes ?? [])
        {
            if (mounts.Any(m => m.Target == path)) continue;
            var name = $"gb-{slug}-{DockerRunParser.Segment(path)}";
            var unique = name;
            for (var n = 2; mounts.Any(m => m.Source == unique); n++) unique = $"{name}-{n}";
            mounts.Add(new MountSetting(MountKind.Volume, unique, path));
        }
        return mounts;
    }

    private static List<MountSetting> Renamed(IReadOnlyList<MountSetting> mounts, string from, string to)
    {
        if (from == to || from.Length == 0) return mounts.ToList();
        var prefix = $"gb-{from}-";
        return mounts.Select(m => m.Kind == MountKind.Volume && m.Source.StartsWith(prefix, StringComparison.Ordinal) ? m with { Source = $"gb-{to}-{m.Source[prefix.Length..]}" } : m).ToList();
    }

    /// <summary>The web marker: the first TCP port the address rules call web (not a database's port, not SSH, SMTP and the like).</summary>
    private static int? WebPortOf(IReadOnlyList<PortRule> ports, string image)
    {
        var database = ContainerService.IsDatabaseImage(image);
        return ports.FirstOrDefault(p => p.Protocol == "tcp" && AddressBook.IsWeb(p.ContainerPort, "tcp", database, false))?.ContainerPort;
    }

    // The daemon's log driver: "" uses it; json-file never rotates on its own, so it gets 10 MB × 3.
    private async Task<LogSetting> LoggingAsync(CancellationToken ct)
    {
        try
        {
            return (await _images.HostAsync(ct)).LogDriver == "json-file"
                ? new LogSetting("", new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "3" })
                : new LogSetting();
        }
        catch (Exception ex) when (ex is EngineException or DockerApiException or HttpRequestException or TimeoutException or IOException)
        {
            _logger.LogInformation(ex, "Docker's log driver couldn't be read");
            return new LogSetting();
        }
    }

    private async Task<string> SlugAsync(string name, CancellationToken ct)
    {
        try
        {
            return (await _steps.ChooseSlugAsync(name, ct)).Slug;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EngineException or DockerApiException or HttpRequestException or TimeoutException)
        {
            _logger.LogInformation(ex, "No slug could be chosen for {Name} yet", name);
            return AppService.Slugify(name) is { Length: > 0 } slug ? slug : "app";
        }
    }

    private async Task<System.Text.Json.Nodes.JsonObject?> LocalAsync(string image, CancellationToken ct)
    {
        try
        {
            return await _images.InspectAsync(image, ct);
        }
        catch (Exception ex) when (ex is EngineException or DockerApiException or HttpRequestException or TimeoutException)
        {
            _logger.LogInformation(ex, "Couldn't look for {Image} on the host", image);
            return null;
        }
    }

    private async Task<string> LanHostAsync(CancellationToken ct)
    {
        try
        {
            return await _steps.LanHostAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "The host's LAN address couldn't be read");
            return _options.PublicHost ?? "this host";
        }
    }

    private sealed class LiveSteps(AppService apps, AppCatalog catalog, PortChanger changer, NetworkService network, HostPortMap hostPorts, ActivityLog activity,
        SettingsService settings) : ICreateSteps
    {
        public Task<UpdatePolicy> DefaultPolicyAsync(CancellationToken ct) => UpdateWatcher.DefaultPolicyAsync(settings, ct);

        public Task<NetworkName> ChooseSlugAsync(string name, CancellationToken ct) => apps.ChooseSlugAsync(name, ct);

        // The address every page uses: the saved one, GITBERTH_PUBLIC_HOST, then the detected one.
        public Task<string> LanHostAsync(CancellationToken ct) => apps.GetPublicHostAsync("http://localhost/");

        public Task<IReadOnlyList<HostPortUse>> HostPortsAsync(CancellationToken ct) => hostPorts.GetAsync(ct);

        public Task<int> SuggestAsync(int containerPort, string protocol, IEnumerable<int> alsoTaken, CancellationToken ct) =>
            hostPorts.SuggestAsync(containerPort, protocol, null, alsoTaken, ct);

        public async Task<IReadOnlySet<string>> NetworksAsync(CancellationToken ct)
        {
            var overview = await network.ListAsync(ct);
            if (overview.Error is { } error) throw new InvalidOperationException($"Docker didn't list its networks ({error}).");
            return overview.Networks.Select(n => n.Network.Name).ToHashSet(StringComparer.Ordinal);
        }

        public Task<AppDefinition> AddAsync(NewAppRow row, CancellationToken ct) => catalog.AddAsync(row, ct);
        public Task SyncAsync(int appId, CancellationToken ct) => catalog.SyncAsync(appId, ct);
        public Task<PortChangePlan> PlanCreateAsync(NewContainerRequest request, CancellationToken ct) => changer.PlanCreateAsync(request, ct);
        public PortChangeJob Start(PortChangePlan plan) => changer.Start(plan);

        public Task RememberAsync(string container, string joined, string? alias, string? ipv4, CancellationToken ct) =>
            network.RememberAsync(container, joined, alias, ipv4, ct);

        public Task ActivityAsync(string container, int? appId, ActivityKind kind, string text, ActivityTone tone, CancellationToken ct) =>
            activity.AddAsync(container, appId, kind, text, tone, ct);

        public Task DiscardAsync(int appId, CancellationToken ct) => catalog.DiscardAsync(appId, ct);

        public async Task<bool> ExistsAsync(string container, CancellationToken ct) => await network.InspectAsync(container, ct) is not null;
    }
}
