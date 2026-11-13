using System.Net;
using System.Text.Json;
using Gitberth.Apps;
using Gitberth.Data;
using Gitberth.Images;
using Gitberth.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Deploy hooks: their tokens, which apps can have them, and their URL's answers. On CT 101's containers (<see cref="AppsRig"/>):
/// orb is a Git app, postgres runs Docker Hub's postgres:18, the samples run images built on the host. A Git app's deploy is
/// only queued here, never run; an image app's update is stood in for (UpdateServiceTests has the update itself).
/// </summary>
public sealed class DeployHookTests : IDisposable
{
    private sealed class Updates : IHookUpdates
    {
        public List<(string Key, string Hook)> Calls { get; } = [];
        public bool Busy { get; set; }

        public Task<Task?> UpdateFromHookAsync(string key, string hookName, CancellationToken ct = default)
        {
            Calls.Add((key, hookName));
            return Task.FromResult(Busy ? null : (Task?)Task.CompletedTask);
        }
    }

    private readonly AppsRig _rig = new();
    private readonly DeployHooks _hooks;
    private readonly Updates _updates = new();
    private readonly DeployHookEndpoint _endpoint;

    public DeployHookTests()
    {
        _hooks = new DeployHooks(_rig.Db, _rig.Protector, _rig.Catalog);
        _endpoint = new DeployHookEndpoint(_hooks, _rig.Actions, _updates, _rig.Activity, NullLogger<DeployHookEndpoint>.Instance);
    }

    public void Dispose() => _rig.Dispose();

    // Docker Hub has postgres:18: Gitberth pulled it, or a check found it there.
    private async Task PostgresFromDockerHubAsync() =>
        await _rig.AddAsync(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = "sha256:" + new string('d', 64), CheckedAt = DateTime.UtcNow });

    private async Task<HookView> CreateAsync(string key, string name)
    {
        var made = await _hooks.CreateAsync(key, name);
        Assert.Null(made.Problem);
        return made.Hook!;
    }

    private async Task<List<Deployment>> DeploymentsAsync(int appId)
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.Deployments.AsNoTracking().Where(d => d.AppId == appId).OrderBy(d => d.Id).ToListAsync();
    }

    private async Task<List<string>> ActivityAsync(string container)
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.Activity.AsNoTracking().Where(a => a.ContainerName == container).OrderBy(a => a.Id).Select(a => a.Text).ToListAsync();
    }

    private async Task<DeployHook?> RowAsync(int hookId)
    {
        await using var db = _rig.Db.CreateDbContext();
        return await db.DeployHooks.AsNoTracking().FirstOrDefaultAsync(h => h.Id == hookId);
    }

    private static JsonElement Json(DeployHookEndpoint.Answer answer) => JsonDocument.Parse(answer.Body!.ToJsonString()).RootElement;

    // ---- Tokens ----

    [Fact]
    public void A_token_is_32_random_bytes_in_base64url_without_padding()
    {
        var token = DeployHooks.NewToken();
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        Assert.NotEqual(token, DeployHooks.NewToken());
        Assert.True(DeployHooks.LooksLikeToken(token));
        Assert.Equal(64, DeployHooks.Hash(token).Length);
        Assert.Equal(DeployHooks.Hash(token), DeployHooks.Hash(token));
        Assert.Matches("^[0-9a-f]{64}$", DeployHooks.Hash(token));
        foreach (var bad in new[] { null, "", "short", "has a space in it, somewhere here", "with/a/slash/abcdefghijklmnop", new string('a', 101) })
            Assert.False(DeployHooks.LooksLikeToken(bad));
    }

    [Fact]
    public void Its_urls_are_built_on_gitberths_address_and_on_the_webhooks_host_name()
    {
        Assert.Equal("http://192.168.50.10:8080/hooks/deploy/abc", DeployHooks.LanUrl("http://192.168.50.10:8080/", "abc"));
        Assert.Equal("http://192.168.50.10:8080/hooks/deploy/abc", DeployHooks.LanUrl("http://192.168.50.10:8080", "abc"));
        Assert.Equal("https://hooks.example.com/hooks/deploy/abc", DeployHooks.PublicUrl("hooks.example.com", "abc"));
    }

    [Fact]
    public async Task A_hook_is_found_by_its_tokens_hash_and_its_url_can_be_shown_again()
    {
        var hook = await CreateAsync("gb-orb", " Contentful ");

        Assert.Equal("Contentful", hook.Name);
        Assert.True(DeployHooks.LooksLikeToken(hook.Token));
        var row = (await RowAsync(hook.Id))!;
        Assert.Equal((_rig.ObsId, DeployHooks.Hash(hook.Token!)), (row.AppId, row.TokenHash));
        // Kept encrypted, never as it is.
        Assert.DoesNotContain(hook.Token!, row.ProtectedToken, StringComparison.Ordinal);
        Assert.Equal(hook.Token, _rig.Protector.Unprotect(row.ProtectedToken));
        Assert.Equal(hook.Token, Assert.Single(await _hooks.ListAsync(_rig.ObsId)).Token);

        var found = await _hooks.FindAsync(hook.Token);
        Assert.Equal((hook.Id, _rig.ObsId), (found!.Value.Hook.Id, found.Value.App.Id));
        Assert.Null(await _hooks.FindAsync(DeployHooks.NewToken()));
        Assert.Null(await _hooks.FindAsync(row.TokenHash));
    }

    [Fact]
    public async Task A_hook_whose_key_is_gone_is_listed_without_its_url_and_still_works()
    {
        var hook = await CreateAsync("gb-orb", "CI");
        await using (var db = _rig.Db.CreateDbContext())
            await db.DeployHooks.Where(h => h.Id == hook.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.ProtectedToken, "lost"));

        Assert.Null(Assert.Single(await _hooks.ListAsync(_rig.ObsId)).Token);
        Assert.NotNull(await _hooks.FindAsync(hook.Token));
    }

    [Fact]
    public async Task Names_are_one_to_fifty_characters_and_one_of_each_per_app_and_ten_at_most()
    {
        Assert.Equal("Give it a name, like Contentful or Nightly.", (await _hooks.CreateAsync("gb-orb", "  ")).Problem);
        Assert.Equal("Keep it to 50 characters.", (await _hooks.CreateAsync("gb-orb", new string('x', 51))).Problem);
        await CreateAsync("gb-orb", new string('x', 50));
        await CreateAsync("gb-orb", "Contentful");
        Assert.Equal("It has a deploy hook called contentful already.", (await _hooks.CreateAsync("gb-orb", "contentful")).Problem);
        // Another app may have one by the same name.
        await CreateAsync("gb-kanban", "Contentful");

        for (var i = 3; i <= DeployHooks.MaxPerApp; i++) await CreateAsync("gb-orb", $"Hook {i}");
        Assert.Equal("An app has at most 10 deploy hooks. Delete one first.", (await _hooks.CreateAsync("gb-orb", "One more")).Problem);
        Assert.Equal(10, (await _hooks.ListAsync(_rig.ObsId)).Count);
    }

    [Fact]
    public async Task A_deleted_hook_is_gone_and_only_its_own_apps_can_delete_it()
    {
        var hook = await CreateAsync("gb-orb", "CI");
        Assert.False(await _hooks.DeleteAsync(_rig.KanbanId, hook.Id));
        Assert.True(await _hooks.DeleteAsync(_rig.ObsId, hook.Id));
        Assert.Empty(await _hooks.ListAsync(_rig.ObsId));
        Assert.Null(await _hooks.FindAsync(hook.Token));
    }

    // ---- Which apps can have them ----

    [Fact]
    public async Task Git_apps_and_apps_on_a_registrys_tag_can_have_hooks()
    {
        Assert.Equal(HookKind.Git, DeployHooks.KindOf((await _rig.Catalog.GetAsync("gb-orb", ""))!.Summary, null));
        await PostgresFromDockerHubAsync();
        var postgres = (await _rig.Catalog.GetAsync("postgres", ""))!;
        Assert.Null(postgres.Row);
        Assert.Equal(HookKind.Image, DeployHooks.KindOf(postgres.Summary, postgres.Row));

        // A container without a row is adopted, as any edit adopts it.
        var hook = await CreateAsync("postgres", "CI");
        var row = (await _rig.RowAsync(a => a.Container == "postgres"))!;
        Assert.Equal(AppSource.Existing, row.Source);
        Assert.Single(await _hooks.ListAsync(row.Id), h => h.Id == hook.Id);
    }

    [Fact]
    public async Task Previews_local_images_digest_pins_and_gitberth_itself_cant_have_hooks()
    {
        // postgres:18 before anything says a registry has it: no hook, as for any local image.
        Assert.Equal("postgres can't have deploy hooks: only Git apps and apps that run a registry's tag can.", (await _hooks.CreateAsync("postgres", "CI")).Problem);
        Assert.Null(await _rig.RowAsync(a => a.Container == "postgres"));
        // Built on this host.
        Assert.NotNull((await _hooks.CreateAsync("sample-bun-hono", "CI")).Problem);
        // Pinned to a digest: nothing to pull.
        await PostgresFromDockerHubAsync();
        _rig.Engine.AddLike("postgres", "pinned", configImage: "postgres@sha256:" + new string('d', 64));
        Assert.NotNull((await _hooks.CreateAsync("pinned", "CI")).Problem);
        // Gitberth would recreate itself under the call.
        Assert.NotNull((await _hooks.CreateAsync("gitberth", "CI")).Problem);
        // A preview builds what its pull request has: it's deployed by its own commits.
        var preview = await _rig.AddAsync(new AppDefinition { Name = "orb · PR #1", Slug = "orb-pr-1", Owner = "acme", Repo = "orb", ParentAppId = _rig.ObsId });
        Assert.NotNull((await _hooks.CreateAsync("gb-orb-pr-1", "CI")).Problem);
        Assert.Null(DeployHooks.KindOf((await _rig.Catalog.GetAsync("gb-orb-pr-1", ""))!.Summary, await _rig.RowAsync(a => a.Id == preview)));
        Assert.Equal("There's no app called nothing-here.", (await _hooks.CreateAsync("nothing-here", "CI")).Problem);
    }

    // ---- Calling a Git app's hook ----

    [Fact]
    public async Task A_git_apps_hook_queues_a_full_build_and_answers_201_with_the_deploy()
    {
        var hook = await CreateAsync("gb-orb", "Contentful");

        var answer = await _endpoint.CallAsync(hook.Token, "203.0.113.5");

        Assert.Equal(201, answer.Status);
        var deploy = Assert.Single(await DeploymentsAsync(_rig.ObsId));
        Assert.Equal((DeployTrigger.Hook, "Contentful", DeployStatus.Queued, (string?)null), (deploy.Trigger, deploy.HookName, deploy.Status, deploy.CommitSha));
        var json = Json(answer);
        Assert.Equal((deploy.Id, "orb", "queued"), (json.GetProperty("deployment").GetInt32(), json.GetProperty("app").GetString(), json.GetProperty("status").GetString()));
        Assert.Equal([$"Deploy hook \"Contentful\" started deploy #{deploy.Id}"], await ActivityAsync("gb-orb"));
        Assert.NotNull((await RowAsync(hook.Id))!.LastUsedAt);
        // Its trigger names the hook wherever a deploy's trigger shows.
        Assert.Equal("Deploy hook \"Contentful\"", Format.Trigger(deploy));
        Assert.Equal("deploy hook \"Contentful\"", Format.TriggerInLine(deploy));
        Assert.Equal("Deploy hook", Format.Trigger(DeployTrigger.Hook));

        // The commit already runs: it's built again all the same, as what changed is the content.
        Assert.Equal(201, (await _endpoint.CallAsync(hook.Token, "203.0.113.5")).Status);
        Assert.Equal(2, (await DeploymentsAsync(_rig.ObsId)).Count(d => d.Trigger == DeployTrigger.Hook));
    }

    [Fact]
    public async Task A_burst_of_calls_leaves_one_build_running_and_the_newest_queued()
    {
        var hook = await CreateAsync("gb-orb", "CI");
        // A build of orb is running: the queue's worker is busy with it, so what comes meanwhile waits in its one slot.
        await _rig.AddAsync(new Deployment { AppId = _rig.ObsId, Trigger = DeployTrigger.Poll, Status = DeployStatus.Building, StartedAt = DateTime.UtcNow });

        var answers = new List<DeployHookEndpoint.Answer>();
        for (var i = 0; i < 5; i++) answers.Add(await _endpoint.CallAsync(hook.Token, "203.0.113.5"));

        Assert.All(answers, a => Assert.Equal(201, a.Status));
        var newest = answers.Select(a => Json(a).GetProperty("deployment").GetInt32()).Max();
        // The newest wins the slot; the ones it took the place of are cancelled as superseded.
        var settled = await UntilAsync(async () =>
        {
            var hooked = (await DeploymentsAsync(_rig.ObsId)).Where(d => d.Trigger == DeployTrigger.Hook).ToList();
            return hooked.Count(d => d.Status == DeployStatus.Cancelled) == 4 ? hooked : null;
        });
        Assert.Equal(newest, Assert.Single(settled, d => d.Status == DeployStatus.Queued).Id);
        Assert.All(settled.Where(d => d.Id != newest), d => Assert.Equal("Superseded by a newer deploy", d.Error));
        var active = (await DeploymentsAsync(_rig.ObsId)).Where(d => d.IsActive).ToList();
        Assert.Equal([DeployStatus.Building, DeployStatus.Queued], active.Select(d => d.Status));
    }

    [Fact]
    public async Task A_stopped_git_app_isnt_started_by_its_hook()
    {
        var hook = await CreateAsync("gb-orb", "Nightly");
        await using (var db = _rig.Db.CreateDbContext())
            await db.Apps.Where(a => a.Id == _rig.ObsId).ExecuteUpdateAsync(s => s.SetProperty(a => a.DesiredState, DesiredState.Stopped));

        Assert.Equal(409, (await _endpoint.CallAsync(hook.Token, "203.0.113.5")).Status);

        Assert.Empty(await DeploymentsAsync(_rig.ObsId));
        Assert.Equal(DesiredState.Stopped, (await _rig.RowAsync(a => a.Id == _rig.ObsId))!.DesiredState);
        Assert.Equal(["Deploy hook \"Nightly\": the app is stopped, so nothing was deployed"], await ActivityAsync("gb-orb"));
    }

    [Fact]
    public async Task An_app_being_deleted_gets_409_and_its_hooks_go_with_it()
    {
        var hook = await CreateAsync("gb-orb", "CI");
        var other = await CreateAsync("gb-kanban", "CI");
        // Holds the delete while it removes orb's container.
        var removing = new TaskCompletionSource();
        var held = new TaskCompletionSource();
        _rig.Engine.BeforeCommand = line =>
        {
            if (line != "rm -f gb-orb") return Task.CompletedTask;
            held.TrySetResult();
            return removing.Task;
        };

        var deleting = _rig.Actions.DeleteAsync(_rig.ObsId, removeImages: false, removeVolumes: false);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(409, (await _endpoint.CallAsync(hook.Token, "203.0.113.5")).Status);
        Assert.Empty(await DeploymentsAsync(_rig.ObsId));

        removing.SetResult();
        await deleting;
        // The row went, and its hooks with it: the URL is unknown from then on. Another app's stay.
        Assert.Null(await RowAsync(hook.Id));
        Assert.Equal(404, (await _endpoint.CallAsync(hook.Token, "203.0.113.5")).Status);
        Assert.NotNull(await RowAsync(other.Id));
    }

    [Fact]
    public async Task A_hook_goes_with_its_apps_row()
    {
        var hook = await CreateAsync("gb-orb", "CI");

        await _rig.Domains.DeleteAppAsync(_rig.ObsId);

        Assert.Null(await RowAsync(hook.Id));
        await using var db = _rig.Db.CreateDbContext();
        Assert.False(await db.DeployHooks.AnyAsync());
    }

    // ---- Calling an image app's hook ----

    [Fact]
    public async Task An_image_apps_hook_starts_its_update_and_answers_202()
    {
        await PostgresFromDockerHubAsync();
        var hook = await CreateAsync("postgres", "CI");

        var answer = await _endpoint.CallAsync(hook.Token, "203.0.113.5");

        Assert.Equal(202, answer.Status);
        Assert.Equal([("postgres", "CI")], _updates.Calls);
        var json = Json(answer);
        Assert.Equal(("postgres", "updating"), (json.GetProperty("app").GetString(), json.GetProperty("status").GetString()));
        // Nothing to deploy: it's no Git app. The update writes its own line once it knows what it did.
        Assert.DoesNotContain(await ActivityAsync("postgres"), l => l.StartsWith("Deploy hook", StringComparison.Ordinal));
        await using var db = _rig.Db.CreateDbContext();
        Assert.False(await db.Deployments.AnyAsync());
    }

    [Fact]
    public async Task An_image_app_already_updating_or_changing_gets_409()
    {
        await PostgresFromDockerHubAsync();
        var hook = await CreateAsync("postgres", "CI");
        _updates.Busy = true;

        Assert.Equal(409, (await _endpoint.CallAsync(hook.Token, "203.0.113.5")).Status);
        Assert.Contains("Deploy hook \"CI\": an update or change of it was under way, so nothing more was done", await ActivityAsync("postgres"));
    }

    // ---- Unknown tokens ----

    [Fact]
    public async Task Unknown_tokens_get_404_then_429_but_a_good_one_always_works()
    {
        var hook = await CreateAsync("gb-orb", "CI");
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        _endpoint.Now = () => now;

        for (var i = 0; i < WebhookThrottle.Limit; i++)
            Assert.Equal(404, (await _endpoint.CallAsync(i % 2 == 0 ? DeployHooks.NewToken() : "nope", "198.51.100.7")).Status);
        Assert.Equal(429, (await _endpoint.CallAsync(DeployHooks.NewToken(), "198.51.100.7")).Status);
        // The hook's own token, from that same sender: it works.
        Assert.Equal(201, (await _endpoint.CallAsync(hook.Token, "198.51.100.7")).Status);
        // Another sender: a plain 404. And once the window has passed, this one's too.
        Assert.Equal(404, (await _endpoint.CallAsync(DeployHooks.NewToken(), "198.51.100.8")).Status);
        now += WebhookThrottle.Window;
        Assert.Equal(404, (await _endpoint.CallAsync(DeployHooks.NewToken(), "198.51.100.7")).Status);
        Assert.Single(await DeploymentsAsync(_rig.ObsId));
    }

    // ---- The URL itself ----

    // A call as Gitberth gets it, with its status-code pages on, as they are for every other path.
    private static DefaultHttpContext Request(string method, string? token, byte[]? body = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/hooks/deploy/" + token;
        if (token is not null) context.Request.RouteValues["token"] = token;
        context.Request.Body = new MemoryStream(body ?? []);
        context.Response.Body = new MemoryStream();
        context.Features.Set<IStatusCodePagesFeature>(new StatusCodePagesFeature());
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
        return context;
    }

    private static string BodyOf(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    public async Task Anything_but_post_gets_a_bare_405_and_never_gitberths_pages(string method)
    {
        var hook = await CreateAsync("gb-orb", "CI");
        var context = Request(method, hook.Token);

        await _endpoint.HandleAsync(context);

        Assert.Equal(405, context.Response.StatusCode);
        Assert.Equal("POST", context.Response.Headers.Allow.ToString());
        Assert.False(context.Features.Get<IStatusCodePagesFeature>()!.Enabled);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Empty(await DeploymentsAsync(_rig.ObsId));
    }

    [Fact]
    public async Task A_post_answers_json_and_reads_no_more_than_64_kb_of_its_body()
    {
        var hook = await CreateAsync("gb-orb", "CMS");
        var body = new byte[200 * 1024];
        var context = Request("POST", hook.Token, body);

        await _endpoint.HandleAsync(context);

        Assert.Equal(201, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.Equal("queued", JsonDocument.Parse(BodyOf(context)).RootElement.GetProperty("status").GetString());
        Assert.Equal(DeployHookEndpoint.MaxBody, context.Request.Body.Position);
        Assert.False(context.Features.Get<IStatusCodePagesFeature>()!.Enabled);
    }

    [Fact]
    public async Task An_unknown_token_gets_a_bare_404_counted_by_cloudflares_visitor_address()
    {
        async Task<int> CallAsync(string? token, string? visitor)
        {
            var context = Request("POST", token);
            if (visitor is not null) context.Request.Headers["CF-Connecting-IP"] = visitor;
            await _endpoint.HandleAsync(context);
            Assert.Equal(0, context.Response.Body.Length);
            Assert.False(context.Features.Get<IStatusCodePagesFeature>()!.Enabled);
            return context.Response.StatusCode;
        }

        // Every call through the tunnel comes from cloudflared's address: the throttle counts the visitor Cloudflare names.
        for (var i = 0; i < WebhookThrottle.Limit; i++) Assert.Equal(404, await CallAsync(DeployHooks.NewToken(), "198.51.100.7"));
        Assert.Equal(429, await CallAsync(DeployHooks.NewToken(), "198.51.100.7"));
        Assert.Equal(404, await CallAsync(DeployHooks.NewToken(), "198.51.100.8"));
        // Without the header, the connection's own address counts; no token at all is just as unknown.
        Assert.Equal(404, await CallAsync(null, null));
    }

    [Fact]
    public async Task Every_path_under_hooks_deploy_is_mapped_for_every_method_anonymous_and_without_antiforgery()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        DeployHookEndpoint.Map(app);

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>(),
            e => e.RoutePattern.RawText == DeployHookEndpoint.Route);
        // No method of its own: routing never answers 405 for it, so status-code pages never turn one into a page.
        Assert.Null(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>());
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.False(endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
    }

    // Polls until a condition holds, for what settles in the background (a superseded deploy marked cancelled).
    private static async Task<T> UntilAsync<T>(Func<Task<T?>> check, int timeoutMs = 10_000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await check() is { } done) return done;
            if (DateTime.UtcNow > until) throw new TimeoutException("It didn't settle in time.");
            await Task.Delay(25);
        }
    }
}
