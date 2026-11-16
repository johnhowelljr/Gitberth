using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gitberth.Agents;
using Gitberth.Agents.Tools;
using Gitberth.Audit;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gitberth.Tests;

/// <summary>
/// What the tools refuse and bound, on CT 101's containers (<see cref="AgentsRig"/>): production builds only commits of its
/// own branch, never a ref an agent names; an Operate token turns previews on or off and nothing more about them; a grep
/// can't tie Gitberth up and logs come back bounded; a tool a token isn't allowed is audited at most once a minute; a
/// change never returns a value that was secret; a restart policy with tries changes to one without; and a wait that runs
/// out is an ordinary answer.
/// </summary>
public sealed class AgentGuardTests : IDisposable
{
    private const string Secret = "s3cr3t-route-7f4e9a";
    private const string NotASha = "commit is a commit's SHA: 7 to 40 hex characters, like 1d2948c.";

    private readonly AgentsRig _agents = new();

    public void Dispose() => _agents.Dispose();

    private static readonly ApiCaller Admin = new(1, "ci-agent", ApiAccess.Admin, AuditVia.Api, "10.0.0.9");
    private static readonly ApiCaller Operator = new(2, "ops-agent", ApiAccess.Operate, AuditVia.Mcp, "10.0.0.8");
    private static readonly ApiCaller Reader = new(3, "reader", ApiAccess.Read, AuditVia.Mcp, "10.0.0.7");

    private Task<(int Status, string Body)> CallAsync(string tool, object? args, ApiCaller caller)
    {
        var dict = args is null ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        return AgentEndpoints.InvokeAsync(ToolCatalog.Find(tool) ?? throw new InvalidOperationException(tool), caller, dict, _agents.Services, CancellationToken.None);
    }

    private static string Error(string body) => JsonNode.Parse(body)!["error"]!.GetValue<string>();

    // A failure's message without the test's secret values in it.
    private static string Scrubbed(string body, params string[] secrets) => secrets.Aggregate(body, (b, s) => b.Replace(s, "<secret>", StringComparison.Ordinal));

    // What AppActions put on the deploy queue for the app: the rig's queue never runs it (its lifetime is stopping).
    private DeployRequest? Queued(int appId)
    {
        var queue = (DeployQueue)typeof(AppActions).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(f => f.FieldType == typeof(DeployQueue)).GetValue(_agents.Rig.Actions)!;
        var channels = (Dictionary<int, Channel<DeployRequest>>)typeof(DeployQueue).GetField("_channels", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        return channels.TryGetValue(appId, out var channel) && channel.Reader.TryPeek(out var request) ? request : null;
    }

    private async Task<AppDefinition> RowAsync(int id)
    {
        await using var db = _agents.Rig.Db.CreateDbContext();
        return await db.Apps.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private async Task<int> DeploymentsAsync(int appId)
    {
        await using var db = _agents.Rig.Db.CreateDbContext();
        return await db.Deployments.CountAsync(d => d.AppId == appId);
    }

    // ---- deploy_app: production builds its own branch ----

    [Fact]
    public async Task A_commit_is_queued_as_a_commit_of_the_apps_own_branch()
    {
        var orb = _agents.Rig.ObsId;
        var (status, body) = await CallAsync("deploy_app", new { app = "orb", commit = "ABCDEF1234567890" }, Operator);

        Assert.True(status == 200, body);
        var request = Queued(orb) ?? throw new InvalidOperationException("Nothing was queued.");
        // The branch and the commit: the pipeline checks it's on the branch (or ran before) when it fetches it.
        Assert.Equal(("refs/heads/main", "abcdef1234567890"), (request.GitRef, request.Commit));
        Assert.Equal(DeployPipeline.OwnBranchRef(await RowAsync(orb)), request.GitRef);
        // Without a commit, the branch's newest, as ever.
        var schema = AgentDocs.Arguments(ToolCatalog.Find("deploy_app")!.InputSchema).Select(a => a.Name).ToList();
        Assert.Equal(["app", "no_cache", "commit", "wait"], schema);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("v1.2.0")]
    [InlineData("refs/pull/7/head")]
    [InlineData("abc12")]
    [InlineData("1d2948cc693a1d2948cc693a1d2948cc693a1d2948cc")]
    [InlineData("1d2948c --upload-pack=touch")]
    public async Task Anything_but_a_commits_sha_is_refused_with_400_and_nothing_is_queued(string commit)
    {
        var orb = _agents.Rig.ObsId;
        var before = await DeploymentsAsync(orb);

        var (status, body) = await CallAsync("deploy_app", new { app = "orb", commit }, Operator);

        Assert.Equal(400, status);
        Assert.Equal(NotASha, Error(body));
        Assert.Null(Queued(orb));
        Assert.Equal(before, await DeploymentsAsync(orb));
    }

    [Fact]
    public async Task A_ref_is_refused_so_no_pull_request_or_other_branch_reaches_production()
    {
        var orb = _agents.Rig.ObsId;
        var before = await DeploymentsAsync(orb);
        // What the review found queued for production by an Operate token: a pull request's head with any commit, and a bare
        // branch name the fetch refused only after it was queued.
        var (status, body) = await CallAsync("deploy_app", new { app = "orb", @ref = "refs/pull/7/head", commit = "abcdef1234567890" }, Operator);
        Assert.Equal(400, status);
        Assert.StartsWith("deploy_app doesn't take ref. It takes app, ", Error(body));
        (status, _) = await CallAsync("deploy_app", new { app = "orb", @ref = "main" }, Operator);
        Assert.Equal(400, status);

        Assert.Null(Queued(orb));
        Assert.Equal(before, await DeploymentsAsync(orb));
        // Nothing about the app's previews was read or changed.
        await using var db = _agents.Rig.Db.CreateDbContext();
        Assert.Empty(await db.Previews.AsNoTracking().Where(p => p.ParentAppId == orb).ToListAsync());
        Assert.False((await RowAsync(orb)).PreviewForks);
    }

    // ---- previews: on or off with Operate, the rest with Admin ----

    private async Task SetPreviewsAsync(int appId, PreviewMode mode, bool forks = false, bool protect = true, int limit = 3, int days = 7)
    {
        await using var db = _agents.Rig.Db.CreateDbContext();
        var row = await db.Apps.SingleAsync(a => a.Id == appId);
        (row.PreviewMode, row.PreviewForks, row.ProtectPreviews, row.PreviewLimit, row.PreviewExpiryDays) = (mode, forks, protect, limit, days);
        await db.SaveChangesAsync();
    }

    private async Task<int> AddPreviewAsync(int parentId)
    {
        await using var db = _agents.Rig.Db.CreateDbContext();
        var preview = new AppDefinition { Name = "orb · PR #7", Slug = "orb-pr-7", Owner = "acme", Repo = "orb", ParentAppId = parentId, AutoDeploy = false };
        db.Apps.Add(preview);
        await db.SaveChangesAsync();
        db.Previews.Add(new PreviewInfo { AppId = preview.Id, ParentAppId = parentId, Kind = PreviewKind.PullRequest, PullRequest = 7, Branch = "feature", Ref = "refs/pull/7/head" });
        await db.SaveChangesAsync();
        return preview.Id;
    }

    [Fact]
    public async Task An_operate_token_turns_previews_on_and_nothing_else_about_them_changes()
    {
        var orb = _agents.Rig.ObsId;
        await SetPreviewsAsync(orb, PreviewMode.Off);
        var tool = ToolCatalog.Find("set_previews")!;
        Assert.Equal(["app", "mode", "confirm"], AgentDocs.Arguments(tool.InputSchema).Select(a => a.Name));
        Assert.Equal((ApiAccess.Operate, true), (tool.Access, tool.Destructive));

        // What the review found an Operate token could do: forks on, protection off, the limits at their most.
        var (refused, why) = await CallAsync("set_previews", new { app = "orb", mode = "pull_requests", forks = true, protect = false, limit = 20, expiry_days = 365 }, Operator);
        Assert.Equal(400, refused);
        Assert.StartsWith("set_previews doesn't take ", Error(why));
        Assert.Equal(PreviewMode.Off, (await RowAsync(orb)).PreviewMode);

        var (status, body) = await CallAsync("set_previews", new { app = "orb", mode = "pull_requests" }, Operator);
        Assert.True(status == 200, body);
        var after = await RowAsync(orb);
        Assert.Equal((PreviewMode.PullRequests, false, true, 3, 7), (after.PreviewMode, after.PreviewForks, after.ProtectPreviews, after.PreviewLimit, after.PreviewExpiryDays));
        // The rest is set_preview_settings', which needs Admin.
        Assert.Equal(ApiAccess.Admin, ToolCatalog.Find("set_preview_settings")!.Access);
        Assert.Contains("needs Admin", AgentEndpoints.Refusal(Operator, ToolCatalog.Find("set_preview_settings")!, null));
    }

    [Fact]
    public async Task Turning_previews_off_while_the_app_has_some_needs_its_name_in_confirm_and_leaves_them_until_then()
    {
        var orb = _agents.Rig.ObsId;
        await SetPreviewsAsync(orb, PreviewMode.PullRequests);
        var preview = await AddPreviewAsync(orb);

        var (status, body) = await CallAsync("set_previews", new { app = "orb", mode = "off" }, Operator);
        Assert.Equal(400, status);
        Assert.Equal("To turn previews off and remove the preview of orb, pass confirm: \"orb\" (the app's name), to show it's meant.", Error(body));
        (status, _) = await CallAsync("set_previews", new { app = "orb", mode = "off", confirm = "kanban" }, Operator);
        Assert.Equal(400, status);

        Assert.Equal(PreviewMode.PullRequests, (await RowAsync(orb)).PreviewMode);
        await using var db = _agents.Rig.Db.CreateDbContext();
        Assert.True(await db.Apps.AnyAsync(a => a.Id == preview));
        Assert.True(await db.Previews.AnyAsync(p => p.AppId == preview));
    }

    [Fact]
    public async Task Previews_turned_off_with_none_there_or_another_mode_need_no_confirm()
    {
        var orb = _agents.Rig.ObsId;
        await SetPreviewsAsync(orb, PreviewMode.PullRequests);
        var (status, body) = await CallAsync("set_previews", new { app = "orb", mode = "pull_requests_and_branches" }, Operator);
        Assert.True(status == 200, body);
        (status, body) = await CallAsync("set_previews", new { app = "orb", mode = "off" }, Operator);
        Assert.True(status == 200, body);
        Assert.Equal(PreviewMode.Off, (await RowAsync(orb)).PreviewMode);
    }

    [Fact]
    public async Task An_admin_token_changes_the_preview_settings_and_is_told_what_forks_and_a_copy_of_production_mean()
    {
        var orb = _agents.Rig.ObsId;
        await SetPreviewsAsync(orb, PreviewMode.PullRequests);
        var tool = ToolCatalog.Find("set_preview_settings")!;
        // The Previews section's banners, in the description and in the answer.
        Assert.Contains(AppAdminTools.ForkWarning, tool.Description);
        Assert.Contains(AppAdminTools.CopyWarning, tool.Description);

        var (status, body) = await CallAsync("set_preview_settings", new { app = "orb", forks = true, limit = 5, databases = "copy_of_production" }, Admin);

        Assert.True(status == 200, body);
        var after = await RowAsync(orb);
        Assert.Equal((PreviewMode.PullRequests, true, true, 5, 7, PreviewDatabaseMode.CopyOfProduction),
            (after.PreviewMode, after.PreviewForks, after.ProtectPreviews, after.PreviewLimit, after.PreviewExpiryDays, after.PreviewDatabases));
        var warnings = JsonNode.Parse(body)!["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains(AppAdminTools.ForkWarning, warnings);
        Assert.Contains(AppAdminTools.CopyWarning, warnings);

        // Out of range is refused before anything is saved.
        foreach (var bad in new object[] { new { app = "orb", limit = 21 }, new { app = "orb", expiry_days = 0 }, new { app = "orb", databases = "all" }, new { app = "orb" } })
        {
            (status, body) = await CallAsync("set_preview_settings", bad, Admin);
            Assert.True(status == 400, body);
        }
        Assert.Equal((5, 7), ((await RowAsync(orb)).PreviewLimit, (await RowAsync(orb)).PreviewExpiryDays));
    }

    // ---- grep and logs ----

    private static List<string> LogLinesOf(int count) =>
        Enumerable.Range(0, count).Select(i => $"2026-11-16T10:00:{i % 60:00}.123456789Z GET /api/items?page={i} 200 12ms ok").ToList();

    [Fact]
    public void A_grep_that_would_backtrack_for_ever_runs_over_5000_lines_in_under_2_seconds()
    {
        var lines = LogLinesOf(5000);
        var sw = Stopwatch.StartNew();
        var shown = AppReadTools.Filter(lines, AppReadTools.Matcher("/(.+)+!/"), CancellationToken.None);
        sw.Stop();
        Assert.Empty(shown);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"It took {sw.Elapsed}.");
        // It still matches what it should, as quickly.
        Assert.Equal(5000, AppReadTools.Filter(lines, AppReadTools.Matcher("/(.+)+ OK$/"), CancellationToken.None).Count);
        Assert.Single(AppReadTools.Filter(lines, AppReadTools.Matcher("page=4999 "), CancellationToken.None));
    }

    [Fact]
    public void A_grep_is_at_most_200_characters_and_one_that_needs_backtracking_is_refused_with_why()
    {
        Assert.True(AppReadTools.Matcher(new string('a', AppReadTools.MaxGrep))("x" + new string('A', 200)));
        Assert.Equal("grep is at most 200 characters.", Assert.Throws<ToolError>(() => AppReadTools.Matcher(new string('a', 201))).Message);
        foreach (var pattern in new[] { @"/(a)\1/", "/(?=a)b/", "/(?<!a)b/", "/(?>a+)b/" })
            Assert.Equal("grep's regular expression can't use backreferences, lookarounds, atomic groups or conditionals here.",
                Assert.Throws<ToolError>(() => AppReadTools.Matcher(pattern)).Message);
        Assert.StartsWith("grep isn't a regular expression .NET can read", Assert.Throws<ToolError>(() => AppReadTools.Matcher("/(/")).Message);
    }

    [Fact]
    public void A_grep_stops_as_soon_as_the_call_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var read = 0;
        IEnumerable<string> Endless()
        {
            for (var i = 0; ; i++)
            {
                if (++read == 100) cts.Cancel();
                yield return "line " + i;
            }
        }
        Assert.Throws<OperationCanceledException>(() => AppReadTools.Filter(Endless(), _ => true, cts.Token));
        Assert.Equal(100, read);
    }

    [Fact]
    public void Log_lines_are_cut_at_8_kb_and_kept_to_about_1_mb_the_oldest_going_first()
    {
        // Nothing to cut: as they were.
        var (same, cut) = LogLines.Of(["a", "b"]);
        Assert.Equal(["a", "b"], same);
        Assert.False(cut);

        // A line longer than 8 KB ends with "…" at 8 KB; the others stay whole.
        var (lines, truncated) = LogLines.Of([new string('x', 20_000), "short"]);
        Assert.True(truncated);
        Assert.Equal(new string('x', LogLines.MaxLine) + "…", lines[0]);
        Assert.Equal("short", lines[1]);
        // Never between the two halves of a surrogate pair.
        var emoji = new string('x', LogLines.MaxLine - 1) + "\U0001F600" + "tail";
        Assert.Equal(new string('x', LogLines.MaxLine - 1) + "…", LogLines.Of([emoji]).Lines[0]);

        // 200 lines of 8 KB are 1.6 MB: the newest 1 MB stay.
        var many = Enumerable.Range(0, 200).Select(i => i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('y', LogLines.MaxLine - 3)).ToList();
        var (kept, dropped) = LogLines.Of(many);
        Assert.True(dropped);
        Assert.Equal(LogLines.MaxTotal / LogLines.MaxLine, kept.Count);
        Assert.Equal(many.TakeLast(kept.Count), kept);
        Assert.True(kept.Sum(l => l.Length) <= LogLines.MaxTotal);
    }

    [Fact]
    public async Task Get_deployment_greps_the_last_5000_lines_and_bounds_the_lines_it_returns()
    {
        var orb = _agents.Rig.ObsId;
        int id;
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            var dep = new Deployment { AppId = orb, Status = DeployStatus.Succeeded, CommitSha = "1d2948cc693a", FinishedAt = DateTime.UtcNow };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        var path = DeployPaths.LogFile(_agents.Rig.Options, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var log = Enumerable.Range(0, 6000).Select(i => i == 100 ? "needle early" : i == 5500 ? "needle late" : $"step {i}").ToList();
        log[^1] = new string('z', 20_000);
        await File.WriteAllLinesAsync(path, log);

        var (status, body) = await CallAsync("get_deployment", new { deployment_id = id, grep = "needle" }, Reader);
        Assert.True(status == 200, body);
        Assert.Equal(["needle late"], JsonNode.Parse(body)!["deployment"]!["log_tail"]!.AsArray().Select(l => l!.GetValue<string>()));

        (status, body) = await CallAsync("get_deployment", new { deployment_id = id, tail = 2 }, Reader);
        Assert.True(status == 200, body);
        var deployment = JsonNode.Parse(body)!["deployment"]!;
        Assert.True(deployment["truncated"]!.GetValue<bool>());
        var last = deployment["log_tail"]!.AsArray()[^1]!.GetValue<string>();
        Assert.Equal(new string('z', LogLines.MaxLine) + "…", last);
    }

    // ---- refusals in the audit log ----

    [Fact]
    public void A_tool_a_token_isnt_allowed_is_audited_once_a_minute_per_token_and_tool_with_the_count_in_between()
    {
        var now = new DateTime(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
        var denials = new ToolDenials { Now = () => now };
        var audit = new AuditLog();
        var deploy = ToolCatalog.Find("deploy_app")!;

        for (var i = 0; i < 200; i++) Assert.NotNull(AgentEndpoints.Refusal(Reader, deploy, audit, denials));
        var first = Assert.Single(audit.Entries);
        Assert.Equal("Refused deploy_app to the token reader: it has Read access, and deploy_app needs Operate.", first.Summary);
        // Another tool, or another token, is counted on its own.
        AgentEndpoints.Refusal(Reader, ToolCatalog.Find("stop_app")!, audit, denials);
        AgentEndpoints.Refusal(Reader with { TokenId = 9, Name = "other" }, deploy, audit, denials);
        Assert.Equal(3, audit.Entries.Count);

        now = now.AddSeconds(59);
        AgentEndpoints.Refusal(Reader, deploy, audit, denials);
        Assert.Equal(3, audit.Entries.Count);
        now = now.AddSeconds(2);
        AgentEndpoints.Refusal(Reader, deploy, audit, denials);
        Assert.Equal(4, audit.Entries.Count);
        var counted = Assert.Single(audit.Entries, e => e.Summary.Contains("more time", StringComparison.Ordinal));
        Assert.Equal("Refused deploy_app to the token reader: it has Read access, and deploy_app needs Operate. " +
            "It was refused 200 more times since the last entry about it, not recorded one by one.", counted.Summary);
        Assert.Equal(new AuditChange("Refused since the last entry", null, "200"), Assert.Single(counted.Changes));
        Assert.Equal((AuditOutcome.Denied, "deploy_app"), (counted.Outcome, counted.Target));

        // The count starts again from there.
        now = now.AddMinutes(2);
        AgentEndpoints.Refusal(Reader, deploy, audit, denials);
        Assert.Equal(5, audit.Entries.Count);
        Assert.Single(audit.Entries, e => e.Summary.Contains("more time", StringComparison.Ordinal));
    }

    // ---- secrets in a change's answer ----

    [Fact]
    public async Task Set_env_vars_never_returns_a_value_that_was_secret_when_it_unmarks_changes_or_removes_one()
    {
        var orb = _agents.Rig.ObsId;
        const string other = "0ther-s3cr3t-11aa", gone = "g0ne-s3cr3t-22bb";
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            db.EnvVars.Add(new EnvVar { AppId = orb, Key = "PAYMENT_ROUTE", ProtectedValue = _agents.Rig.Protector.Protect(Secret), IsSecret = true });
            db.EnvVars.Add(new EnvVar { AppId = orb, Key = "ROUTE_B", ProtectedValue = _agents.Rig.Protector.Protect(other), IsSecret = true });
            db.EnvVars.Add(new EnvVar { AppId = orb, Key = "ROUTE_C", ProtectedValue = _agents.Rig.Protector.Protect(gone), IsSecret = true });
            await db.SaveChangesAsync();
        }

        var (status, body) = await CallAsync("set_env_vars", new
        {
            app = "orb",
            set = new object[] { new { name = "PAYMENT_ROUTE", value = "now-plain", secret = false }, new { name = "ROUTE_B", value = "new-b" } },
            remove = new[] { "ROUTE_C" },
            next_deploy = true,
        }, Admin);

        Assert.True(status == 200, Scrubbed(body, Secret, other, gone));
        var settings = JsonNode.Parse(body)!["changes"]!.AsArray().Select(c => c!["setting"]!.GetValue<string>()).ToList();
        Assert.Contains("PAYMENT_ROUTE", settings);
        Assert.Contains("ROUTE_B", settings);
        Assert.Contains("Removed ROUTE_C", settings);
        foreach (var value in new[] { Secret, other, gone }) Assert.False(body.Contains(value, StringComparison.Ordinal), Scrubbed(body, Secret, other, gone));
    }

    // ---- restart policy ----

    [Fact]
    public async Task A_restart_policy_with_tries_changes_to_one_without_and_tries_go_with_on_failure_only()
    {
        var inspect = _agents.Rig.Engine.Inspect("postgres");
        ((JsonObject)inspect["HostConfig"]!)["RestartPolicy"] = new JsonObject { ["Name"] = "on-failure", ["MaximumRetryCount"] = 3 };

        var (status, body) = await CallAsync("update_app_settings", new { app = "postgres", restart_policy = "always", restart_retries = 2 }, Admin);
        Assert.Equal(400, status);
        Assert.Equal("restart_retries goes with the on-failure restart policy only.", Error(body));
        (status, body) = await CallAsync("update_app_settings", new { app = "postgres", restart_retries = -1 }, Admin);
        Assert.Equal(400, status);
        Assert.Equal("restart_retries is 0 or more.", Error(body));
        Assert.Empty(_agents.Rig.Engine.Writes);

        // As the Settings tab does it: another policy has no tries.
        (status, body) = await CallAsync("update_app_settings", new { app = "postgres", restart_policy = "always" }, Admin);
        Assert.True(status == 200, body);
        var change = JsonNode.Parse(body)!["done"]![0]!["changes"]!.AsArray().Single(c => c!["setting"]!.GetValue<string>() == "Restart policy")!;
        Assert.Equal(("On failure, 3 tries", "Always"), (change["before"]!.GetValue<string>(), change["after"]!.GetValue<string>()));
    }

    // ---- waiting ----

    [Fact]
    public async Task A_wait_that_runs_out_is_an_ordinary_answer_saying_it_is_still_running()
    {
        // A change in the background (an update, a recreate) that's still going: null, which the tools say as still running.
        var never = new TaskCompletionSource<string>();
        Assert.Null(await AgentContext.WaitAsync(never.Task, CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.Equal("done", await AgentContext.WaitAsync(Task.FromResult("done"), CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AgentContext.WaitAsync(never.Task, cancelled.Token, TimeSpan.FromSeconds(5)));

        // A deploy still running when the wait runs out: its number and status, still running, never an error.
        int id;
        await using (var db = _agents.Rig.Db.CreateDbContext())
        {
            var dep = new Deployment { AppId = _agents.Rig.ObsId, Status = DeployStatus.Building };
            db.Deployments.Add(dep);
            await db.SaveChangesAsync();
            id = dep.Id;
        }
        var ctx = _agents.Services.GetRequiredService<AgentContext>();
        (ctx.DeployWaitLimit, ctx.PollEvery) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20));
        var answer = JsonNode.Parse(ToolJson.Write(await ctx.WaitForDeployAsync(id, CancellationToken.None)))!;
        Assert.Equal((id, "Building", true, false), (answer["deployment_id"]!.GetValue<int>(), answer["status"]!.GetValue<string>(),
            answer["still_running"]!.GetValue<bool>(), answer["finished"]!.GetValue<bool>()));
        Assert.Contains("still running", answer["hint"]!.GetValue<string>());
    }
}
