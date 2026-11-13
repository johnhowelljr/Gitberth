using System.Net;
using System.Text;
using Gitberth.Apps;
using Gitberth.Components.Apps;
using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Health;
using Gitberth.Notifications;
using Gitberth.Previews;
using Gitberth.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Build filters (Watch paths and Ignored paths, gitignore-style), the commit-message markers, which deploys may be
/// skipped, and how everything that reads a deploy's status takes a skipped one: GitHub, notifications, Activity, the
/// dashboard, previews and the watcher. Also the build cache's size and clearing. The pipeline's own skips are in
/// <see cref="BuildFilterPipelineTests"/>.
/// </summary>
public sealed class BuildFilterTests
{
    // ---- The matcher ----

    [Theory]
    // No slash: a name at any depth.
    [InlineData("*.md", "README.md", true)]
    [InlineData("*.md", "docs/guide/a.md", true)]
    [InlineData("*.md", "docs/a.mdx", false)]
    [InlineData("*.md", "md", false)]
    [InlineData("*.md", "notes.md/today.txt", true)]
    [InlineData("README.md", "README.md", true)]
    [InlineData("README.md", "packages/ui/README.md", true)]
    [InlineData("README.md", "readme.md", false)]
    [InlineData("README.md", "README.md.bak", false)]
    [InlineData("docs", "docs", true)]
    [InlineData("docs", "docs/a.md", true)]
    [InlineData("docs", "site/docs/a.md", true)]
    [InlineData("docs", "mydocs/a.md", false)]
    [InlineData(".env", ".env", true)]
    [InlineData("package.json", "web/package.json", true)]
    // A trailing slash: a folder, and everything under it, at any depth.
    [InlineData("docs/", "docs/a.md", true)]
    [InlineData("docs/", "site/docs/a.md", true)]
    [InlineData("docs/", "docs", false)]
    [InlineData("docs/", "docsite/a.md", false)]
    [InlineData(".github/", ".github/workflows/ci.yml", true)]
    [InlineData("website/", "apps/website/index.html", true)]
    // A leading slash, or one in the middle: anchored to the root.
    [InlineData("/website/", "website/index.html", true)]
    [InlineData("/website/", "website/css/site.css", true)]
    [InlineData("/website/", "apps/website/index.html", false)]
    [InlineData("/website/", "website", false)]
    [InlineData("/package.json", "package.json", true)]
    [InlineData("/package.json", "web/package.json", false)]
    [InlineData("docs/api", "docs/api", true)]
    [InlineData("docs/api", "docs/api/v1.md", true)]
    [InlineData("docs/api", "site/docs/api/v1.md", false)]
    [InlineData("/docs/*.md", "docs/a.md", true)]
    [InlineData("/docs/*.md", "docs/sub/a.md", false)]
    [InlineData("src/*.js", "src/app.js", true)]
    [InlineData("src/*.js", "src/lib/app.js", false)]
    [InlineData("./docs", "docs/a.md", true)]
    [InlineData("./docs", "site/docs/a.md", false)]
    // **: any number of folders.
    [InlineData("docs/**", "docs/a.md", true)]
    [InlineData("docs/**", "docs/deep/er/a.md", true)]
    [InlineData("docs/**", "docs", false)]
    [InlineData("docs/**", "src/docs/a.md", false)]
    [InlineData("**/test/**", "test/a.py", true)]
    [InlineData("**/test/**", "src/pkg/test/a.py", true)]
    [InlineData("**/test/**", "src/testing/a.py", false)]
    [InlineData("**/foo", "foo", true)]
    [InlineData("**/foo", "a/b/foo", true)]
    [InlineData("src/**/*.ts", "src/app.ts", true)]
    [InlineData("src/**/*.ts", "src/a/b/app.ts", true)]
    [InlineData("src/**/*.ts", "src/app.tsx", false)]
    [InlineData("src/**/*.ts", "lib/src/app.ts", false)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("**", "anything/at/all.txt", true)]
    [InlineData("*", "anything/at/all.txt", true)]
    // ** inside a segment is one star.
    [InlineData("a**b", "axxb", true)]
    [InlineData("a**b", "a/b", false)]
    // ?: one character, never a slash.
    [InlineData("a?c.txt", "abc.txt", true)]
    [InlineData("a?c.txt", "ac.txt", false)]
    [InlineData("a?c.txt", "a/c.txt", false)]
    // Everything else as it's written: brackets (Next.js routes), spaces, and \ before a special character.
    [InlineData("pages/[id].tsx", "pages/[id].tsx", true)]
    [InlineData("pages/[id].tsx", "pages/i.tsx", false)]
    [InlineData("my docs/", "my docs/a.md", true)]
    [InlineData(@"\#notes.md", "#notes.md", true)]
    [InlineData(@"file\*.txt", "file*.txt", true)]
    [InlineData(@"file\*.txt", "fileX.txt", false)]
    [InlineData("  *.md  ", "README.md", true)]
    public void A_pattern_matches_like_a_gitignore_line(string pattern, string path, bool matches) =>
        Assert.Equal(matches, PathPattern.Parse(pattern).Matches(path));

    [Fact]
    public void A_long_pattern_of_stars_matches_quickly()
    {
        var pattern = PathPattern.Parse(string.Concat(Enumerable.Repeat("*a", 99)) + "b");
        var path = new string('a', 5000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(pattern.Matches(path));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_pattern_that_cant_be_used_never_matches()
    {
        Assert.False(PathPattern.Parse("/").Matches("anything"));
        Assert.False(PathPattern.Parse("../x").Matches("x"));
    }

    // ---- Fields and their checks ----

    [Fact]
    public void Blank_lines_and_comments_are_left_out_and_the_rest_trimmed()
    {
        Assert.Equal(["src/", "*.md", @"\#hash"], BuildFilters.Patterns("\r\n  src/  \n# the docs don't deploy\n\n*.md\r\n\\#hash\n"));
        Assert.Empty(BuildFilters.Patterns(null));
        Assert.Empty(BuildFilters.Patterns("  \n# only a comment\n"));
        Assert.False(BuildFilters.Any(null, "# nothing\n"));
        Assert.True(BuildFilters.Any(null, "*.md"));
    }

    [Fact]
    public void A_field_is_kept_without_trailing_blanks_or_blank_ends()
    {
        Assert.Equal("src/\n\n*.md", BuildFilters.Normalize("\r\n\r\nsrc/   \r\n\r\n*.md\t\r\n\r\n"));
        Assert.Null(BuildFilters.Normalize(" \n \n"));
        Assert.Null(BuildFilters.Normalize(null));
        // Kept as it is once it's normalized, so a field that wasn't touched never counts as changed.
        Assert.Equal("src/\n*.md", BuildFilters.Normalize(BuildFilters.Normalize("src/\n*.md\n")));
    }

    [Fact]
    public void Patterns_that_can_be_used_pass()
    {
        Assert.Null(BuildFilters.Problem(null, "Watch paths"));
        Assert.Null(BuildFilters.Problem("src/\n/website/\n**/*.ts\n# a comment\n\n\\!important.txt", "Watch paths"));
        // 50 patterns are fine, however many comments and blank lines come with them.
        var fifty = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"dir{i}/")) + "\n# one\n# two\n\n";
        Assert.Null(BuildFilters.Problem(fifty, "Watch paths"));
        Assert.Null(BuildFilters.Problem(new string('a', BuildFilters.MaxLength), "Watch paths"));
    }

    [Theory]
    [InlineData("src/\n!src/generated/", "Watch paths: line 2 starts with !, and patterns that bring files back aren't supported.")]
    [InlineData("/", "Watch paths: line 1 (/) doesn't name a file or folder.")]
    [InlineData("./", "Watch paths: line 1 (./) doesn't name a file or folder.")]
    [InlineData("../shared/", "Watch paths: line 1 (../shared/) leaves the repo")]
    [InlineData("docs//api", "Watch paths: line 1 (docs//api) has an empty folder name (//).")]
    public void A_pattern_that_cant_be_used_is_refused_with_why(string text, string said) =>
        Assert.StartsWith(said, BuildFilters.Problem(text, "Watch paths"));

    [Fact]
    public void At_most_fifty_patterns_of_two_hundred_characters_each()
    {
        var many = string.Join('\n', Enumerable.Range(1, 51).Select(i => $"dir{i}/"));
        Assert.Equal("Ignored paths: up to 50 patterns, and there are 51.", BuildFilters.Problem(many, "Ignored paths"));
        Assert.Equal("Ignored paths: line 2 is longer than 200 characters.", BuildFilters.Problem("*.md\n" + new string('a', 201), "Ignored paths"));
    }

    // ---- The markers ----

    [Theory]
    [InlineData("Fix a typo [skip ci]", "[skip ci]")]
    [InlineData("Fix a typo\n\nThe body says [CI SKIP] at the end", "[CI SKIP]")]
    [InlineData("[Skip Deploy] Update the docs", "[Skip Deploy]")]
    [InlineData("Docs [deploy skip]", "[deploy skip]")]
    [InlineData("Docs [skip gitberth]", "[skip gitberth]")]
    [InlineData("Docs [no ci]", "[no ci]")]
    // The first one in the message is the one quoted.
    [InlineData("[no ci] and [skip ci]", "[no ci]")]
    [InlineData("Skip ci without brackets", null)]
    [InlineData("[skipci]", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_marker_anywhere_in_the_message_in_any_case_asks_not_to_deploy(string? message, string? marker)
    {
        Assert.Equal(marker, BuildFilters.MarkerIn(message));
        var verdict = BuildFilters.ForMessage(message);
        if (marker is null) Assert.Null(verdict);
        else Assert.Equal(new SkipVerdict(SkipKind.Asked, $"The commit message says {marker}"), verdict);
    }

    // ---- Which deploys, and the verdict on a commit's files ----

    [Fact]
    public void Only_a_new_commit_the_watcher_or_github_started_may_be_skipped()
    {
        foreach (var trigger in Enum.GetValues<DeployTrigger>())
            Assert.Equal(trigger is DeployTrigger.Poll or DeployTrigger.Webhook, BuildFilters.CanSkip(trigger));
    }

    [Fact]
    public void Watch_paths_let_a_commit_through_only_when_it_changes_a_file_they_match()
    {
        const string watch = "src/\npackage.json";
        Assert.Equal(new SkipVerdict(SkipKind.Filtered, "Only files the build filters leave out changed: docs/a.md, README.md"),
            BuildFilters.ForChanges(["docs/a.md", "README.md"], watch, null));
        Assert.Null(BuildFilters.ForChanges(["docs/a.md", "src/app.ts"], watch, null));
        Assert.Null(BuildFilters.ForChanges(["web/package.json"], watch, null));
        Assert.Equal("src/app.ts", BuildFilters.Relevant(["docs/a.md", "src/app.ts"], watch, null));
    }

    [Fact]
    public void Ignored_paths_leave_a_commit_out_when_every_file_it_changed_matches_them()
    {
        const string ignored = "*.md\ndocs/";
        Assert.NotNull(BuildFilters.ForChanges(["README.md", "docs/logo.png"], null, ignored));
        Assert.Null(BuildFilters.ForChanges(["README.md", "app.py"], null, ignored));
    }

    [Fact]
    public void Both_filters_together_watch_a_folder_without_its_ignored_files()
    {
        Assert.NotNull(BuildFilters.ForChanges(["website/README.md", "api/main.go"], "/website/", "*.md"));
        Assert.Null(BuildFilters.ForChanges(["website/README.md", "website/index.html"], "/website/", "*.md"));
    }

    [Fact]
    public void A_skip_names_the_first_five_files_and_counts_the_rest()
    {
        var files = Enumerable.Range(1, 7).Select(i => $"docs/{i}.md").ToList();
        Assert.Equal("Only files the build filters leave out changed: docs/1.md, docs/2.md, docs/3.md, docs/4.md, docs/5.md and 2 more",
            BuildFilters.ForChanges(files, "src/", null)!.Reason);
        // A commit that changed nothing doesn't build anything new either.
        Assert.Equal("Nothing changed since the running commit", BuildFilters.ForChanges([], "src/", null)!.Reason);
    }

    // ---- git's answers ----

    [Fact]
    public void The_whole_message_is_read_with_the_commit()
    {
        var commit = GitClient.ParseCommit("abc1234def\u001fAda\u001f2026-11-12T10:00:00+00:00\u001fUpdate the docs\u001fUpdate the docs\n\nNothing to ship [skip ci]\n\n");
        Assert.Equal(("abc1234def", "Ada", "Update the docs"), (commit.Sha, commit.Author, commit.Subject));
        Assert.Equal("Update the docs\n\nNothing to ship [skip ci]", commit.FullMessage);
        Assert.Equal(new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc), commit.Date);
        // Without a body read, the subject is all there is.
        Assert.Equal("Only a subject", new CommitInfo("abc", "Ada", DateTime.UtcNow, "Only a subject").FullMessage);
    }

    [Fact]
    public void A_diffs_names_are_read_as_git_wrote_them()
    {
        Assert.Equal(["docs/a.md", "README.md", "with space.txt"], GitClient.ParseNames("docs/a.md\0README.md\0with space.txt\0\n"));
        Assert.Equal(["odd\nname"], GitClient.ParseNames("odd\nname\0\n"));
        Assert.Empty(GitClient.ParseNames(""));
    }

    // ---- Settings ----

    [Fact]
    public void Build_filters_go_with_a_git_apps_source_settings_and_are_kept_normalized()
    {
        var app = new AppDefinition { Branch = "main", WatchPaths = "src/\n*.ts", IgnoredPaths = null };
        var git = GitAppSettings.ToGit(app);
        Assert.Equal(("src/\n*.ts", (string?)null), (git.WatchPaths, git.IgnoredPaths));

        GitAppSettings.Apply(app, git with { WatchPaths = "  \n", IgnoredPaths = "*.md\r\n\r\n" });
        Assert.Equal(((string?)null, "*.md"), (app.WatchPaths, app.IgnoredPaths));
    }

    [Fact]
    public void A_change_of_filters_says_it_changes_nothing_that_runs()
    {
        var before = new GitOptions("main", null, DockerfileMode.Auto, null, null, true, true);
        var after = before with { WatchPaths = "src/\npackage.json\nDockerfile\nyarn.lock", IgnoredPaths = "*.md" };

        var lines = AppSettingsService.GitLines(before, after).ToList();

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(ApplyKind.Gitberth, l.How));
        Assert.Equal(("Watch paths", "any file", "src/, package.json, Dockerfile and 1 more"), (lines[0].Label, lines[0].Before, lines[0].After));
        Assert.Equal(("Ignored paths", "none", "*.md"), (lines[1].Label, lines[1].Before, lines[1].After));
    }

    [Fact]
    public void Only_a_changed_filter_is_checked()
    {
        var before = new GitOptions("main", null, DockerfileMode.Auto, null, null, true, true, IgnoredPaths: "!kept/");
        Assert.Empty(AppSettingsService.GitProblems(before, before));
        var problem = Assert.Single(AppSettingsService.GitProblems(before, before with { WatchPaths = "src/\n!src/gen/" }));
        Assert.StartsWith("Watch paths: line 2 starts with !", problem.Message);
    }

    [Fact]
    public async Task Saving_filters_restarts_and_rebuilds_nothing_and_a_bad_one_saves_nothing()
    {
        using var rig = new AppsRig();
        var orb = (await rig.RowAsync(a => a.Slug == "orb"))!.Id;
        var edit = await EditAsync(rig, orb, g => g with { WatchPaths = "src/\r\npackage.json\r\n", IgnoredPaths = "*.md" });

        var saved = await rig.Apps.SaveSettingsAsync(orb, edit);

        Assert.Equal((false, false, 0, 0), (saved.RecreateNeeded, saved.RebuildNeeded, saved.LiveChanged.Count, saved.Problems.Count));
        var row = (await rig.RowAsync(a => a.Id == orb))!;
        Assert.Equal(("src/\npackage.json", "*.md", false, false), (row.WatchPaths, row.IgnoredPaths, row.ConfigChangePending, row.RebuildPending));

        var refused = await rig.Apps.SaveSettingsAsync(orb, await EditAsync(rig, orb, g => g with { IgnoredPaths = "*.md\n!README.md" }));
        Assert.StartsWith("Ignored paths: line 2 starts with !", Assert.Single(refused.Problems).Message);
        Assert.Equal("*.md", (await rig.RowAsync(a => a.Id == orb))!.IgnoredPaths);
    }

    private static async Task<SettingsEdit> EditAsync(AppsRig rig, int id, Func<GitOptions, GitOptions> git)
    {
        await using var db = rig.Db.CreateDbContext();
        var app = await db.Apps.AsNoTracking().Include(a => a.Ports).Include(a => a.EnvVars).Include(a => a.Volumes).Include(a => a.Networks).AsSplitQuery()
            .SingleAsync(a => a.Id == id);
        var container = GitAppSettings.ToContainer(app, rig.Protector);
        var options = GitAppSettings.ToOptions(app, rig.Health, rig.Restarts);
        var loadedGit = GitAppSettings.ToGit(app);
        return new SettingsEdit(null, container, container, options, options, loadedGit, git(loadedGit));
    }

    [Fact]
    public void An_app_in_a_subfolder_is_offered_its_folder_as_a_watch_path_but_not_given_it()
    {
        var draft = GitOptionsDraft.From(new GitOptions("main", "website", DockerfileMode.Auto, null, null, true, true));
        // Offered, never filled in by itself: a subfolder app can build from the repo's root.
        Assert.Equal("", draft.WatchPaths);
        Assert.Equal("website/", draft.SubfolderSuggestion);

        draft.UseSubfolderSuggestion();
        Assert.Equal("website/", draft.WatchPaths);
        Assert.Null(draft.SubfolderSuggestion);
        Assert.True(draft.WatchChanged);
        Assert.Equal("website/", draft.ToOptions().WatchPaths);

        var withOthers = GitOptionsDraft.From(new GitOptions("main", "apps/web", DockerfileMode.Auto, null, null, true, true, WatchPaths: "package.json"));
        withOthers.UseSubfolderSuggestion();
        Assert.Equal("package.json\napps/web/", withOthers.WatchPaths);
        Assert.Null(GitOptionsDraft.From(new GitOptions("main", null, DockerfileMode.Auto, null, null, true, true)).SubfolderSuggestion);
    }

    [Fact]
    public void The_form_says_whats_wrong_with_a_filter_as_its_typed()
    {
        var draft = GitOptionsDraft.From(new GitOptions("main", null, DockerfileMode.Auto, null, null, true, true, WatchPaths: "src/"));
        Assert.False(draft.Dirty);
        draft.WatchPaths = "src/\r\n";
        // The same patterns, typed again: nothing changed.
        Assert.False(draft.WatchChanged);
        draft.WatchPaths = "src/\n!src/gen/";
        Assert.True(draft.Dirty);
        Assert.StartsWith("Watch paths: line 2", draft.WatchProblem);
        Assert.Null(draft.IgnoredProblem);
        // Not a rebuild: filters only decide which commits deploy by themselves.
        Assert.False(draft.NeedsBuild);
    }

    [Fact]
    public void A_preview_takes_its_parents_build_filters()
    {
        var parent = new AppDefinition { Name = "orb", Slug = "orb", Owner = "acme", Repo = "orb", WatchPaths = "src/", IgnoredPaths = "*.md" };
        var preview = new AppDefinition { Name = "orb · PR #3", Slug = "orb-pr-3", ParentAppId = 1, WatchPaths = "old/" };

        PreviewCopy.Sync(preview, parent, new PreviewInfo { Kind = PreviewKind.PullRequest, PullRequest = 3, Branch = "feature", Ref = "refs/pull/3/head" });

        Assert.Equal(("src/", "*.md"), (preview.WatchPaths, preview.IgnoredPaths));
    }

    // ---- What reads a deploy's status ----

    [Theory]
    [InlineData(SkipKind.Filtered, "Skipped: nothing the app builds from changed")]
    [InlineData(SkipKind.Asked, "Skipped: the commit asked not to be deployed")]
    public void A_skipped_commit_gets_a_success_status_linking_to_its_log(SkipKind kind, string text)
    {
        var status = GitHubReports.Status(DeployStatus.Skipped, null, "Gitberth: Web", "https://web.example.com/", "http://10.1.1.80:8080/apps/1/deployments/7",
            preview: false, kind);
        Assert.Equal(("success", text, "http://10.1.1.80:8080/apps/1/deployments/7"), (status.State, status.Description, status.TargetUrl));
    }

    [Theory]
    [InlineData(DeployTrigger.Poll)]
    [InlineData(DeployTrigger.Webhook)]
    public void A_skipped_commit_isnt_told(DeployTrigger trigger)
    {
        Assert.Null(DeployNotifier.EventFor(new DeployReport(1, 7, trigger, "abc1234def", DeployStatus.Skipped, null, SkipKind.Filtered)));
        Assert.Null(DeployNotifier.EventFor(new DeployReport(1, 7, trigger, "abc1234def", DeployStatus.Skipped, null, SkipKind.Asked)));
    }

    [Fact]
    public void A_skipped_deploy_never_makes_a_preview_look_failed_or_building()
    {
        var skipped = new Deployment { Status = DeployStatus.Skipped, SkipReason = "The commit message says [skip ci]" };
        Assert.Equal((PreviewState.Ready, "Ready"), PreviewStates.Of(new PreviewFacts(skipped, Running: true, DesiredState.Running, "img", 1, 3)));
        Assert.Equal((PreviewState.Stopped, "Not running"), PreviewStates.Of(new PreviewFacts(skipped, Running: false, DesiredState.Running, "img", 1, 3)));
        Assert.Equal(PreviewState.Waiting, PreviewStates.Of(new PreviewFacts(skipped, Running: false, DesiredState.Running, null, 1, 3)).State);
    }

    [Fact]
    public void A_skipped_commit_is_never_retried()
    {
        var now = DateTime.UtcNow;
        var app = new AppDefinition { CurrentCommit = "aaa", LastAttemptedCommit = "bbb" };
        Assert.False(RepoWatcher.ShouldRetry(app, "bbb", new LastAttempt("bbb", DeployStatus.Skipped, DeployTrigger.Poll, "an-older-build", now.AddHours(-1), 0),
            BuildStamp.Current, now));
        Assert.False(RepoWatcher.ShouldDeploy(app, "bbb", deployInProgress: false));
    }

    [Fact]
    public async Task Activity_and_the_dashboard_list_a_skip_neutrally_and_dont_count_it()
    {
        using var db = new ImageKit.Db();
        var dir = Directory.CreateTempSubdirectory("gitberth-skip-").FullName;
        try
        {
            var activity = new ActivityLog(db, new StatsStore(new GitberthOptions { DataDir = dir }));
            var app = await db.AddAsync(new AppDefinition { Name = "orb", Slug = "orb", Source = AppSource.Git });
            int skippedId, builtId;
            await using (var ctx = db.CreateDbContext())
            {
                var built = new Deployment { AppId = app.Id, Trigger = DeployTrigger.Poll, Status = DeployStatus.Succeeded, CommitSha = "1111111aaa", QueuedAt = DateTime.UtcNow.AddMinutes(-20), FinishedAt = DateTime.UtcNow.AddMinutes(-19) };
                var skipped = new Deployment
                {
                    AppId = app.Id, Trigger = DeployTrigger.Poll, Status = DeployStatus.Skipped, CommitSha = "2222222bbb", CommitMessage = "Update the docs",
                    SkipReason = "Only files the build filters leave out changed: docs/a.md", QueuedAt = DateTime.UtcNow.AddMinutes(-5), FinishedAt = DateTime.UtcNow.AddMinutes(-5),
                };
                ctx.Deployments.AddRange(built, skipped);
                await ctx.SaveChangesAsync();
                (skippedId, builtId) = (skipped.Id, built.Id);
            }

            var recent = await activity.RecentDeploysAsync(DateTime.UtcNow.AddDays(-1), 10);
            Assert.Equal(2, recent.Count);
            Assert.True(recent[0].Skipped);
            Assert.Equal($"deploy #{skippedId} skipped: only files the build filters leave out changed: docs/a.md", recent[0].Item.Text);
            Assert.Equal(ActivityTone.Neutral, recent[0].Item.Tone);
            Assert.False(recent[1].Skipped);

            var items = await activity.ForAsync("gb-orb", app.Id);
            var item = items.Single(i => i.DeploymentId == skippedId);
            Assert.Equal($"Deploy #{skippedId} (New commit) skipped: only files the build filters leave out changed: docs/a.md · 2222222 Update the docs", item.Text);
            Assert.Equal(ActivityTone.Neutral, item.Tone);
            Assert.Equal(ActivityTone.Good, items.Single(i => i.DeploymentId == builtId).Tone);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
        }
    }

    // ---- The build cache ----

    private sealed class Engine(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/version" ? Answer(DockerFixtures.Text("version.json")) : answer(request));
        }
    }

    private static HttpResponseMessage Answer(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void The_build_caches_size_is_added_up_as_docker_system_df_does()
    {
        var usage = DockerApi.ParseBuildCache("""
            {"BuildCache":[
              {"ID":"a","Type":"regular","Size":1000,"Shared":false,"InUse":false},
              {"ID":"b","Type":"regular","Size":500,"Shared":true,"InUse":false},
              {"ID":"c","Type":"source.local","Size":250,"Shared":false,"InUse":true}
            ]}
            """);
        Assert.Equal(new BuildCacheUsage(1250, 3), usage);
        Assert.Equal(new BuildCacheUsage(0, 0), DockerApi.ParseBuildCache("""{"BuildCache":null}"""));
        Assert.Equal(7_000_000_000, DockerApi.ParsePrune("""{"CachesDeleted":["a","b"],"SpaceReclaimed":7000000000}"""));
        Assert.Equal(0, DockerApi.ParsePrune("""{"CachesDeleted":null}"""));
    }

    [Fact]
    public async Task The_build_cache_is_read_and_cleared_through_the_engine_api()
    {
        var engine = new Engine(r => r.RequestUri!.AbsolutePath switch
        {
            "/v1.56/system/df" => Answer("""{"BuildCache":[{"ID":"a","Size":2048,"Shared":false}]}"""),
            "/v1.56/build/prune" => Answer("""{"CachesDeleted":["a"],"SpaceReclaimed":1288490188}"""),
            _ => Answer("""{"message":"not here"}""", HttpStatusCode.NotFound),
        });
        using var api = new DockerApi("unix:///var/run/docker.sock", engine);
        using var rig = new AppsRig();
        var cache = new BuildCache(api, rig.Pipeline, NullLogger<BuildCache>.Instance);

        Assert.Equal(new BuildCacheUsage(2048, 1), await cache.UsageAsync());

        // Refused while a build runs: that build would lose its cache halfway.
        using (var build = rig.Pipeline.TryHoldBuilds())
        {
            Assert.NotNull(build);
            Assert.Null(rig.Pipeline.TryHoldBuilds());
            var refused = await cache.ClearAsync();
            Assert.False(refused.Ok);
            Assert.Equal("A build is running, so the cache wasn't cleared. Clear it once the build is done.", refused.Message);
            Assert.DoesNotContain(engine.Calls, c => c.StartsWith("POST", StringComparison.Ordinal));
        }

        var cleared = await cache.ClearAsync();
        Assert.True(cleared.Ok);
        Assert.Equal(1288490188, cleared.Freed);
        Assert.Equal("Cleared the build cache: 1.2 GiB freed. The next builds take longer, since every step runs again.", cleared.Message);
        Assert.Equal(["GET /version", "GET /v1.56/system/df?type=build-cache", "POST /v1.56/build/prune?all=true"], engine.Calls);
        // Builds go on once it's done.
        using var after = rig.Pipeline.TryHoldBuilds();
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Clearing_that_docker_refuses_says_why()
    {
        var engine = new Engine(_ => Answer("""{"message":"a prune operation is already running"}""", HttpStatusCode.Conflict));
        using var api = new DockerApi("unix:///var/run/docker.sock", engine);
        using var rig = new AppsRig();

        var cleared = await new BuildCache(api, rig.Pipeline, NullLogger<BuildCache>.Instance).ClearAsync();

        Assert.False(cleared.Ok);
        Assert.Equal("Docker didn't clear the build cache: a prune operation is already running.", cleared.Message);
        Assert.NotNull(rig.Pipeline.TryHoldBuilds());
    }
}
