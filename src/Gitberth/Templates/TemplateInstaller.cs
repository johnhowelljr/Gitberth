using System.Globalization;
using System.Security.Cryptography;
using Gitberth.Compose;
using Gitberth.Data;
using Gitberth.Databases;
using Gitberth.Docker;
using Gitberth.Domains;
using Gitberth.Network;
using Gitberth.Services;

namespace Gitberth.Templates;

/// <summary>What Install asks for: the template, the app's name (null: the template's, or a free variant of it), its inputs, and a domain.</summary>
public sealed record TemplateRequest(string TemplateId, string? Name, IReadOnlyDictionary<string, string> Inputs, string? Domain = null);

/// <summary>Where an installed app is reached, as its Compose file's built-ins say it (APP_URL, APP_HOST, APP_SCHEME, APP_HTTPS).</summary>
public sealed record TemplateAddress(string Url, string Host, bool Https)
{
    /// <summary>Its domain's address when it has one, else its LAN address on its host port.</summary>
    public static TemplateAddress For(string? domain, string lanHost, int? hostPort) =>
        domain is { Length: > 0 }
            ? new($"https://{domain}", domain, true)
            : new($"http://{AddressBook.HostPart(lanHost)}{(hostPort is int port ? ":" + port.ToString(CultureInfo.InvariantCulture) : "")}", lanHost, false);
}

/// <summary>An install as it would go: the app's name and slug, its LAN port, its address and variables, and what stops it.</summary>
/// <param name="Values">Its variables: the inputs and the generated secrets (marked secret). The built-ins aren't among them: each deploy sets those.</param>
/// <param name="Server">The Postgres server its database goes on; null when it needs none (or there's none: see Problems).</param>
public sealed record TemplatePlan(AppTemplate Template, string Name, string Slug, int? HostPort, TemplateAddress Address, string? Domain,
    IReadOnlyList<ComposeValue> Values, string? Server, IReadOnlyList<string> Problems)
{
    public bool CanInstall => Problems.Count == 0;

    /// <summary>What's wrong with the domain asked for (also among <see cref="Problems"/>); null when it's fine or there's none.</summary>
    public string? DomainProblem { get; init; }

    /// <summary>
    /// There are Postgres servers Gitberth can use, but none is chosen for new databases (Settings › Databases chooses one);
    /// its problem is <see cref="TemplateInstaller.ChooseServer"/>.
    /// </summary>
    public bool NeedsServerChoice { get; init; }

    /// <summary>It can't be installed because there's no Postgres server Gitberth can use at all (one can be set up from the template's page).</summary>
    public bool NeedsServer => Template.NeedsPostgres && Server is null && !NeedsServerChoice;

    /// <summary>
    /// Its domain goes behind Cloudflare Access at install: it's <see cref="AppTemplate.OpenUntilSetup"/>, so until someone
    /// signs in, whoever opened it first could claim it.
    /// </summary>
    public bool ProtectDomain { get; init; }
}

/// <summary>What an install did: the new app and its first deploy, or why there's none.</summary>
/// <param name="Message">What happened, in a sentence (a domain that couldn't be added is said here too).</param>
public sealed record TemplateInstalled(bool Ok, string Message, int? AppId = null, string? Key = null, int? DeploymentId = null);

/// <summary>
/// Installs a template (<see cref="TemplateCatalog"/>): an inline Compose app (<see cref="ComposeApps.CreateInlineAsync"/>)
/// from its file, its inputs and newly generated secrets as the app's variables (the secrets marked secret, never logged),
/// its databases made through the databases feature before anything runs (their connection strings set by Gitberth, in parts
/// too), its domain when one was given (behind Cloudflare Access for a template that's open until someone sets it up), then
/// its first deploy. Each deploy sets its address (APP_URL…: <see cref="TemplateBuiltIns"/>). Afterwards it's a Compose app
/// like any other. Installs go one at a time from plan to create, so two never get the same name or host port.
/// </summary>
/// <param name="docker">Lists Docker's volumes: an earlier install's left behind keep its name taken. Null in tests that don't.</param>
/// <param name="options">Where an earlier install's kept folder would be. Null in tests that don't.</param>
public sealed class TemplateInstaller(TemplateCatalog catalog, ComposeApps compose, AppService apps, AppActions actions, DatabaseService databases,
    DatabaseServers servers, DomainService domains, ILogger<TemplateInstaller> logger, DockerCli? docker = null, GitberthOptions? options = null)
{
    /// <summary>What the template page says when a template needs Postgres and there's no server Gitberth can use at all.</summary>
    public const string NoServer = "It needs a Postgres server for its database, and there's none Gitberth can use yet.";

    /// <summary>What it says when there are servers Gitberth can use, but none is chosen for new databases.</summary>
    public const string ChooseServer =
        "It needs a Postgres server for its database: there are several Gitberth can use, and none is chosen for new databases. Choose one in Settings › Databases.";

    /// <summary>Why a template that's open until set up gets no domain at install while Cloudflare Access can't keep it closed.</summary>
    public const string ClaimDomain = "Add its domain on its Network tab once you've signed in: until then, whoever opens it first can claim it.";

    // One install at a time from its plan to its create: the next plan sees the name and host port the last one took.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How the install would go now, without changing anything.</summary>
    public async Task<TemplatePlan> PlanAsync(TemplateRequest request, string lanHost, CancellationToken ct = default)
    {
        var t = catalog.Find(request.TemplateId) ?? throw new ArgumentException($"There's no template {request.TemplateId}.", nameof(request));
        var problems = new List<string>();
        var (name, slug, left) = await NameAsync(t, request.Name, ct);
        if (AppService.NameProblem(name) is { } nameProblem) problems.Add(nameProblem);
        else if (left.Count > 0)
            problems.Add($"Data an earlier {slug} left is still here ({string.Join(", ", left)}), and its new secrets wouldn't open it: choose another name, or delete that data first.");

        var (domain, domainProblem, protect) = await DomainAsync(t, request.Domain, ct);
        if (domainProblem is not null) problems.Add(domainProblem);

        int? hostPort = null;
        try
        {
            hostPort = await apps.SuggestHostPortAsync(t.Web.Port, "tcp", null, [], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add($"Docker didn't say which host ports are free ({ex.Message}). Try again in a moment.");
        }

        string? server = null;
        var choose = false;
        if (t.NeedsPostgres)
        {
            try
            {
                var listed = await servers.ListAsync(ct);
                server = (await servers.DefaultAsync(listed, ct))?.Container;
                // Several it can use and none chosen is another matter than none at all: setting up one more isn't the answer.
                choose = server is null && listed.Any(s => s.Usable);
                if (server is null) problems.Add(choose ? ChooseServer : NoServer);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
            }
        }

        var address = TemplateAddress.For(domain, lanHost, hostPort);
        var (values, inputProblems) = Values(t, request.Inputs, Generate);
        problems.AddRange(inputProblems);
        return new TemplatePlan(t, name, slug, hostPort, address, domain, values, server, problems)
        {
            DomainProblem = domainProblem, NeedsServerChoice = choose, ProtectDomain = protect,
        };
    }

    // The domain asked for, or why it can't be the app's: its spelling, domains on, nobody else's, and for a template that's
    // open until set up, Cloudflare Access ready to keep it closed meanwhile.
    private async Task<(string? Domain, string? Problem, bool Protect)> DomainAsync(AppTemplate t, string? typed, CancellationToken ct)
    {
        if (typed is not { Length: > 0 } || typed.Trim().Length == 0) return (null, null, false);
        var domain = DomainNames.Normalize(typed);
        var problem = DomainNames.Problem(domain)
            ?? (await domains.EnabledAsync(ct) ? null : "Domains are off: turn them on in Settings › Domains, or install it without one.")
            ?? await domains.UnavailableAsync(null, domain!, ct);
        if (problem is not null) return (null, problem, false);
        if (!t.OpenUntilSetup) return (domain, null, false);
        var protection = await domains.ProtectionAsync(ct);
        return protection.Ready
            ? (domain, null, true)
            : (null, $"{ClaimDomain} (Cloudflare Access would keep it closed meanwhile, but it isn't ready: {protection.Problem?.TrimEnd('.')}.)", false);
    }

    /// <summary>
    /// Installs it: the app, its databases, its domain (protected first, for a template that's open until someone sets it
    /// up), and its first deploy, whose ID the result carries. A database that can't be made takes the app away again, so
    /// nothing half-made stays; a domain that can't be added is said, and the app stays (its Network tab adds one later).
    /// </summary>
    public async Task<TemplateInstalled> InstallAsync(TemplateRequest request, string lanHost, CancellationToken ct = default)
    {
        TemplatePlan plan;
        ComposeCreated created;
        await _gate.WaitAsync(ct);
        try
        {
            plan = await PlanAsync(request, lanHost, ct);
            if (!plan.CanInstall) return new TemplateInstalled(false, string.Join(" ", plan.Problems));
            var t0 = plan.Template;
            created = await compose.CreateInlineAsync(new InlineComposeApp(plan.Name, t0.Compose, t0.Web.Service, t0.Web.Port, plan.Values)
            {
                HostPort = plan.HostPort,
                TemplateId = t0.Id,
                HealthCheckPath = t0.Web.Health,
                EveryServiceOnSharedNetwork = t0.SharedNetwork,
            }, deploy: false, ct);
        }
        catch (ArgumentException ex)
        {
            return new TemplateInstalled(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        var t = plan.Template;

        // Its databases before anything runs: the first deploy sets their connection strings.
        foreach (var d in t.Databases)
        {
            DatabaseDone done;
            try
            {
                done = await databases.AddAsync(created.Key, new DatabaseRequest(d.Kind, d.Variable, d.Kind == DatabaseKind.Postgres ? plan.Server : null), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                done = new DatabaseDone(false, ex.Message);
            }
            if (done.Ok) continue;
            logger.LogWarning("Installing {Template}: its {Kind} database couldn't be made, so {App} is taken away again", t.Id, d.Kind, created.Key);
            await actions.DeleteAsync(created.AppId, removeImages: true, removeVolumes: true, removeDatabases: true);
            return new TemplateInstalled(false, $"Its {DatabaseService.KindText(d.Kind)} database couldn't be made: {done.Message.TrimEnd('.')}. Nothing was kept.");
        }

        var said = plan.Domain is { } domain ? await AddDomainAsync(created.AppId, domain, plan.ProtectDomain) : "";
        var deployment = await actions.DeployAsync(created.AppId, DeployTrigger.Created, CancellationToken.None);
        logger.LogInformation("Installed the template {Template} as {App}; its first deploy is #{Deployment}", t.Id, created.Key, deployment);
        return new TemplateInstalled(true, $"Installed {plan.Name}: its first deploy is under way.{said}", created.AppId, created.Key, deployment);
    }

    // The domain, before the first deploy: behind Cloudflare Access first when the app is open until someone sets it up, and
    // taken away again when it can't be. What to tell about it, as a sentence after the install's; "" when it went well.
    private async Task<string> AddDomainAsync(int appId, string domain, bool protect)
    {
        var added = await domains.AddAsync(appId, domain, CancellationToken.None);
        if (added.Error is { } error) return $" Its domain {domain} wasn't added: {error.TrimEnd('.')}. Add it on its Network tab.";
        if (!protect) return "";
        var (protectError, _) = await domains.SetProtectedAsync(appId, domain, true, CancellationToken.None);
        if (protectError is null) return $" {domain} is behind Cloudflare Access until you turn that off on its Network tab, once you've signed in.";
        // Not protected at all (Access stopped being ready since the plan): it never goes out open.
        if (protectError.Contains("can't be protected yet", StringComparison.Ordinal))
        {
            await domains.RemoveAsync(appId, domain, CancellationToken.None);
            return $" Its domain {domain} wasn't added: {protectError.TrimEnd('.')}. {ClaimDomain}";
        }
        // Saved as protected, its route held back until Access takes it: it's never served open.
        return $" {protectError.TrimEnd('.')}.";
    }

    /// <summary>
    /// The app's name and slug, and what an earlier app of that slug left behind: the name asked for (any leftovers are the
    /// plan's problem), else the template's name, or the first free variant of it ("Uptime Kuma 2") whose slug is its own:
    /// neither another app's name nor a name taken on the network, nor one whose volumes or kept folder an earlier install
    /// left behind (Compose would mount them again, with new secrets that don't open them).
    /// </summary>
    private async Task<(string Name, string Slug, IReadOnlyList<string> Left)> NameAsync(AppTemplate t, string? asked, CancellationToken ct)
    {
        var volumes = docker is null ? null : await docker.VolumeNamesAsync(ct);
        if (asked is { Length: > 0 } && asked.Trim().Length > 0)
        {
            var name = asked.Trim();
            if (AppService.NameProblem(name) is not null) return (name, "", []);
            var slug = (await apps.ChooseSlugAsync(name, ct)).Slug;
            return (name, slug, Leftovers(slug, volumes));
        }
        var taken = (await apps.ListNavAsync(ct)).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        (string, string, IReadOnlyList<string>)? last = null;
        for (var i = 1; i <= 50; i++)
        {
            var name = FreeName(t.Name, i);
            var choice = await apps.ChooseSlugAsync(name, ct);
            var left = Leftovers(choice.Slug, volumes);
            last = (name, choice.Slug, left);
            if (!taken.Contains(name) && choice.Taken is null && left.Count == 0) return (name, choice.Slug, left);
        }
        return last!.Value;
    }

    // An earlier app's volumes (gb-<slug>_…) and kept folder (/data/compose/<slug>/data), left when it was deleted without them.
    private List<string> Leftovers(string slug, IReadOnlySet<string>? volumes)
    {
        var left = (volumes ?? new HashSet<string>()).Where(v => v.StartsWith(ComposeNames.Project(slug) + "_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        if (options is not null && Directory.Exists(ComposeReleases.For(options, slug).DataDir)) left.Add(ComposeReleases.For(options, slug).DataDir);
        return left;
    }

    /// <summary>The template's name for the n-th app of it: "Uptime Kuma", "Uptime Kuma 2"…</summary>
    public static string FreeName(string name, int n) => n <= 1 ? name : $"{name} {n.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The app's variables: each input as given (or its default), checked against its kind, and each secret newly made, marked
    /// secret; nothing here is logged. The built-ins (APP_URL…) aren't among them: each deploy sets those
    /// (<see cref="TemplateBuiltIns"/>), so they follow the app's domains and port.
    /// </summary>
    public static (List<ComposeValue> Values, List<string> Problems) Values(AppTemplate t, IReadOnlyDictionary<string, string> inputs, Func<TemplateSecret, string> secret)
    {
        var values = new List<ComposeValue>();
        var problems = new List<string>();
        foreach (var input in t.Inputs)
        {
            var value = (inputs.TryGetValue(input.Name, out var given) ? given : input.Default ?? "").Trim();
            if (input.Kind == TemplateInputKind.Bool) value = value.ToLowerInvariant() is "true" or "on" or "yes" or "1" ? "true" : "false";
            if (TemplateCatalog.InputProblem(input, value) is { } problem) problems.Add(problem);
            values.Add(new ComposeValue(input.Name, value));
        }
        foreach (var s in t.Secrets) values.Add(new ComposeValue(s.Name, secret(s), Secret: true));
        return (values, problems);
    }

    /// <summary>A new secret: its length of its alphabet's characters, from the system's secure random numbers.</summary>
    public static string Generate(TemplateSecret secret) => RandomNumberGenerator.GetString(secret.Alphabet == SecretAlphabet.Hex
        ? "0123456789abcdef"
        : "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", secret.Length);
}
