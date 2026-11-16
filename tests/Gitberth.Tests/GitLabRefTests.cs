using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Git;
using Gitberth.Previews;
using Gitberth.Security;
using Gitberth.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Gitberth.Tests;

/// <summary>
/// GitLab projects as Gitberth names them: parsed from what's pasted (nested groups, pages of a project, SSH, a self-hosted
/// GitLab under a path), kept in an app's Owner and Repo without ever passing for a GitHub repo, cloned from Settings'
/// GitLab with its token in a header (never the URL), and their merge requests' refs and labels.
/// </summary>
public sealed class GitLabRefTests
{
    private const string GitLabCom = "https://gitlab.com";

    [Theory]
    [InlineData("https://gitlab.com/acme/web", "acme", "web", null)]
    [InlineData("https://gitlab.com/acme/web.git", "acme", "web", null)]
    [InlineData("https://gitlab.com/acme/web/", "acme", "web", null)]
    [InlineData("gitlab.com/acme/web", "acme", "web", null)]
    [InlineData("https://gitlab.com/acme/platform/team/web", "acme/platform/team", "web", null)]
    [InlineData("https://gitlab.com/acme/platform/web/-/tree/main", "acme/platform", "web", "main")]
    [InlineData("https://gitlab.com/acme/platform/web/-/tree/feature/login/src/api", "acme/platform", "web", "feature/login/src/api")]
    [InlineData("https://gitlab.com/acme/web/-/merge_requests/12", "acme", "web", null)]
    [InlineData("https://gitlab.com/acme/web/-/blob/main/README.md", "acme", "web", null)]
    [InlineData("git@gitlab.com:acme/platform/web.git", "acme/platform", "web", null)]
    [InlineData("ssh://git@gitlab.com/acme/web.git", "acme", "web", null)]
    [InlineData("https://GitLab.com/Acme/Web_App.v2", "Acme", "Web_App.v2", null)]
    public void A_gitlab_url_names_a_project_with_its_groups(string input, string ns, string project, string? tree)
    {
        Assert.True(GitLabRepoRef.TryParse(input, GitLabCom, out var parsed, out var error), error);
        Assert.Equal(new GitLabRepoRef(ns, project, tree), parsed);
        Assert.Equal($"{ns}/{project}", parsed!.PathWithNamespace);
        Assert.Equal(Uri.EscapeDataString($"{ns}/{project}"), parsed.ApiId);
    }

    [Theory]
    [InlineData("", "Paste a GitLab project URL.")]
    [InlineData("https://gitlab.com/acme", "The URL needs a group or user and a project, e.g. https://gitlab.com/you/your-app")]
    [InlineData("https://gitlab.example.com/acme/web", "That's on gitlab.example.com, and Gitberth's GitLab is gitlab.com (Settings › GitLab).")]
    [InlineData("git@gitlab.example.com:acme/web.git", "That's on gitlab.example.com, and Gitberth's GitLab is gitlab.com (Settings › GitLab).")]
    [InlineData("https://gitlab.com/acme/-web", "'acme/-web' isn't a valid GitLab project path.")]
    [InlineData("https://gitlab.com/acme/web.atom", "'acme/web.atom' isn't a valid GitLab project path.")]
    public void What_isnt_a_project_on_gitberths_gitlab_says_why(string input, string message)
    {
        Assert.False(GitLabRepoRef.TryParse(input, GitLabCom, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.Equal(message, error);
    }

    [Fact]
    public void A_self_hosted_gitlab_takes_its_own_host_port_and_path()
    {
        const string instance = "http://10.1.1.5:8929/gitlab";
        Assert.True(GitLabRepoRef.TryParse("http://10.1.1.5:8929/gitlab/team/sub/api", instance, out var parsed, out _));
        Assert.Equal(new GitLabRepoRef("team/sub", "api"), parsed);
        Assert.Equal("http://10.1.1.5:8929/gitlab/team/sub/api.git", parsed!.CloneUrl(instance));
        Assert.Equal("http://10.1.1.5:8929/gitlab/team/sub/api", parsed.WebUrl(instance + "/"));
        // Without a scheme it's the instance's, port and all.
        Assert.True(GitLabRepoRef.TryParse("10.1.1.5/gitlab/team/api", instance, out var bare, out _));
        Assert.Equal(new GitLabRepoRef("team", "api"), bare);
        // Another port, or outside GitLab's path, isn't Gitberth's GitLab.
        Assert.False(GitLabRepoRef.TryParse("http://10.1.1.5:9999/gitlab/team/api", instance, out _, out var port));
        Assert.StartsWith("That's on 10.1.1.5:9999", port);
        Assert.False(GitLabRepoRef.TryParse("http://10.1.1.5:8929/team/api", instance, out _, out var outside));
        Assert.Equal("GitLab is at http://10.1.1.5:8929/gitlab: the project's URL starts with that.", outside);
    }

    [Theory]
    [InlineData(null, "https://gitlab.com")]
    [InlineData("", "https://gitlab.com")]
    [InlineData("gitlab.example.com", "https://gitlab.example.com")]
    [InlineData("https://GitLab.Example.com/", "https://gitlab.example.com")]
    [InlineData("http://10.1.1.5:8929/gitlab/", "http://10.1.1.5:8929/gitlab")]
    public void Gitlabs_address_is_kept_plain(string? input, string kept)
    {
        Assert.Equal(kept, GitLabRepoRef.NormalizeInstance(input, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ftp://gitlab.example.com")]
    [InlineData("https://user:pass@gitlab.example.com")]
    [InlineData("https://gitlab.example.com/?x=1")]
    [InlineData("https://")]
    public void A_bad_address_is_refused(string input)
    {
        Assert.Null(GitLabRepoRef.NormalizeInstance(input, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_pasted_url_is_githubs_or_gitberths_gitlabs()
    {
        Assert.True(RepoUrls.TryParse("https://github.com/acme/web/tree/main/api", GitLabCom, out var github, out _));
        Assert.Equal(new GitHubRepoRef("acme", "web", "main/api"), github);
        Assert.Equal(GitHost.GitHub, github!.Host);

        Assert.True(RepoUrls.TryParse("https://gitlab.com/acme/platform/web", GitLabCom, out var gitlab, out _));
        Assert.Equal(GitHost.GitLab, gitlab!.Host);
        Assert.Equal(("gitlab:acme/platform", "web", "acme/platform/web", "web"), (gitlab.AppOwner, gitlab.AppRepo, gitlab.FullName, gitlab.Name));

        // A GitHub URL is never read as a GitLab one, whatever Gitberth's GitLab is.
        Assert.True(RepoUrls.TryParse("github.com/acme/web", "https://gitlab.example.com", out var bare, out _));
        Assert.IsType<GitHubRepoRef>(bare);

        Assert.False(RepoUrls.TryParse("https://bitbucket.org/acme/web", GitLabCom, out _, out var other));
        Assert.Equal("Gitberth deploys from github.com and from its GitLab, gitlab.com. For another GitLab, set its address in Settings › GitLab.", other);
        Assert.False(RepoUrls.TryParse(" ", GitLabCom, out _, out var empty));
        Assert.Equal("Paste a GitHub repository or GitLab project URL.", empty);
    }

    [Fact]
    public void An_app_keeps_a_gitlab_project_apart_from_any_github_repo()
    {
        var project = new GitLabRepoRef("acme/platform", "web");
        Assert.Equal(project, RepoRef.Of(project.AppOwner, project.AppRepo));
        Assert.Equal(new GitHubRepoRef("acme", "web"), RepoRef.Of("acme", "web"));
        Assert.True(RepoRef.IsGitLab("gitlab:acme"));
        Assert.False(RepoRef.IsGitLab("acme"));
        Assert.False(RepoRef.IsGitLab(null));
        // GitHub's acme/web and GitLab's acme/web are two repos wherever Gitberth keys by Owner/Repo.
        var gitlab = RepoRef.Of("gitlab:acme", "web");
        Assert.NotEqual("acme/web", $"{gitlab.AppOwner}/{gitlab.AppRepo}");
        Assert.Equal("acme/web", RepoRef.FullNameOf("gitlab:acme", "web"));

        var app = new AppDefinition { Owner = "gitlab:acme/platform", Repo = "web" };
        Assert.Equal(("acme/platform/web", true), (app.RepoFullName, app.FromGitLab));
        Assert.Equal("https://gitlab.example.com/acme/platform/web", app.RepoWebUrlOn("https://gitlab.example.com"));
        var github = new AppDefinition { Owner = "acme", Repo = "web" };
        Assert.Equal(("acme/web", false, "https://github.com/acme/web"), (github.RepoFullName, github.FromGitLab, github.RepoWebUrlOn("https://gitlab.example.com")));
    }

    [Fact]
    public void A_merge_requests_head_is_listed_and_fetched_like_a_pull_requests()
    {
        var remote = GitClient.ParseLsRemote(
            "ref: refs/heads/main\tHEAD\n1111111111111111111111111111111111111111\tHEAD\n"
            + "1111111111111111111111111111111111111111\trefs/heads/main\n"
            + "2222222222222222222222222222222222222222\trefs/merge-requests/7/head\n"
            + "3333333333333333333333333333333333333333\trefs/merge-requests/7/merge\n"
            + "4444444444444444444444444444444444444444\trefs/merge-requests/12/head\n"
            + "5555555555555555555555555555555555555555\trefs/tags/v1\n");
        Assert.Equal("main", remote.DefaultBranch);
        Assert.Equal([new RemotePull(7, "2222222222222222222222222222222222222222"), new RemotePull(12, "4444444444444444444444444444444444444444")], remote.PullHeads);

        Assert.True(GitClient.IsSafeRef("refs/merge-requests/7/head"));
        Assert.False(GitClient.IsSafeRef("refs/merge-requests/7/merge"));
        Assert.False(GitClient.IsSafeRef("refs/merge-requests/../head"));
        Assert.Equal(7, GitClient.PullNumber("refs/merge-requests/7/head"));
        Assert.Null(GitClient.PullNumber("refs/merge-requests/x/head"));
    }

    [Fact]
    public void A_merge_requests_preview_is_called_as_gitlab_calls_it()
    {
        Assert.Equal("refs/merge-requests/12/head", PreviewNames.Ref(PreviewKind.PullRequest, "feature", 12, GitHost.GitLab));
        Assert.Equal("refs/pull/12/head", PreviewNames.Ref(PreviewKind.PullRequest, "feature", 12));
        Assert.Equal("refs/heads/feature", PreviewNames.Ref(PreviewKind.Branch, "feature", null, GitHost.GitLab));
        Assert.Equal("MR !12", PreviewNames.Label(PreviewKind.PullRequest, "feature", 12, GitHost.GitLab));
        Assert.Equal("PR #12", PreviewNames.Label(PreviewKind.PullRequest, "feature", 12));
        Assert.Equal("orb · MR !12", PreviewNames.Name("orb", PreviewKind.PullRequest, "feature", 12, GitHost.GitLab));
        Assert.Equal(GitHost.GitLab, PreviewNames.HostOfRef("refs/merge-requests/12/head"));
        Assert.Equal(GitHost.GitHub, PreviewNames.HostOfRef("refs/pull/12/head"));
        // Its slug and domain are a pull request's, so names stay as short and as unique.
        Assert.Equal("orb-pr-12", PreviewNames.Slug("orb", PreviewKind.PullRequest, "feature", 12));

        var parent = new AppDefinition { Owner = "gitlab:acme", Repo = "orb" };
        Assert.Equal("https://gitlab.example.com/acme/orb/-/merge_requests/12",
            PreviewService.UrlOf(parent, new PreviewInfo { Kind = PreviewKind.PullRequest, PullRequest = 12, Branch = "feature" }, "https://gitlab.example.com"));
        Assert.Equal("https://gitlab.example.com/acme/orb/-/tree/feature/login",
            PreviewService.UrlOf(parent, new PreviewInfo { Kind = PreviewKind.Branch, Branch = "feature/login" }, "https://gitlab.example.com"));
    }

    [Fact]
    public void Gits_words_for_a_gitlab_project_name_gitlab_and_its_settings()
    {
        var project = new GitLabRepoRef("acme", "web");
        Assert.Equal("acme/web needs a GitLab token. Add one in Settings › GitLab (read_api and read_repository scopes).",
            GitClient.Explain("fatal: could not read Username for 'https://gitlab.com': terminal prompts disabled", project, null, hasToken: false, GitLabCom));
        Assert.Contains("GitLab rejected the token", GitClient.Explain("remote: HTTP Basic: Access denied\nfatal: Authentication failed", project, null, hasToken: true, GitLabCom));
        Assert.Equal("GitLab can't find acme/web. If it's private, add a token in Settings › GitLab.",
            GitClient.Explain("remote: The project you were looking for could not be found or you don't have permission to view it.\nfatal: repository 'https://gitlab.com/acme/web.git/' not found",
                project, null, hasToken: false, GitLabCom));
        Assert.Equal("Branch 'dev' doesn't exist in acme/web.", GitClient.Explain("warning: Could not find remote branch dev to clone.\nfatal: Remote branch dev not found in upstream origin", project, "dev", hasToken: true, GitLabCom));
        // A token without a repository scope gets git's 403 from GitLab; a project its user isn't in, a 404.
        Assert.Equal("GitLab refused the token on acme/web: it needs the read_repository (or api) scope, and its user a role on the project. Check it in Settings › GitLab.",
            GitClient.Explain("fatal: unable to access 'https://gitlab.com/acme/web.git/': The requested URL returned error: 403", project, null, hasToken: true, GitLabCom));
        Assert.StartsWith("GitLab can't find acme/web, or your token can't see it.",
            GitClient.Explain("fatal: unable to access 'https://gitlab.com/acme/web.git/': The requested URL returned error: 404", project, null, hasToken: true, GitLabCom));
        Assert.Equal("GitLab's rate limit for git is used up for the moment: Gitberth tries again later.",
            GitClient.Explain("fatal: unable to access 'https://gitlab.com/acme/web.git/': The requested URL returned error: 429", project, null, hasToken: true, GitLabCom));
        Assert.Equal("Can't reach gitlab.example.com:8443. Check its address in Settings › GitLab, and the host's network and DNS.",
            GitClient.Explain("fatal: unable to access 'https://gitlab.example.com:8443/acme/web.git/': Could not resolve host: gitlab.example.com", project, null, true,
                "https://gitlab.example.com:8443"));
        // GitHub's words are as they were.
        Assert.Contains("needs a GitHub token", GitClient.Explain("fatal: could not read Username for 'https://github.com': terminal prompts disabled", new GitHubRepoRef("acme", "web"), null, false));
    }
}

/// <summary>git as Gitberth runs it for a GitLab project: Settings' GitLab's URL, and its token as oauth2's password in an env-only header.</summary>
public sealed class GitLabGitTests : IDisposable
{
    private sealed class Db(DbContextOptions<GitberthDb> options) : IDbContextFactory<GitberthDb>
    {
        public GitberthDb CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SettingsService _settings;
    private readonly List<(IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)> _ran = [];
    private readonly GitClient _git;

    public GitLabGitTests()
    {
        _connection.Open();
        var db = new Db(new DbContextOptionsBuilder<GitberthDb>().UseSqlite(_connection).Options);
        using (var context = db.CreateDbContext()) context.Database.EnsureCreated();
        _settings = new SettingsService(db, new SecretProtector(new EphemeralDataProtectionProvider()));
        _git = new GitClient(new GitberthOptions { DataDir = Path.GetTempPath() }, _settings)
        {
            Runner = (args, options, _) =>
            {
                lock (_ran) _ran.Add((args, new Dictionary<string, string>(options.Environment ?? new Dictionary<string, string>())));
                return Task.FromResult(new ProcessResult(0, "ref: refs/heads/main\tHEAD\nabc1234abc1234abc1234abc1234abc1234abc12\trefs/heads/main\n", ""));
            },
        };
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_public_project_is_listed_from_gitlab_com_without_a_header()
    {
        await _git.LsRemoteAsync(new GitLabRepoRef("acme/platform", "web"));

        var (args, env) = Assert.Single(_ran);
        Assert.Equal(["ls-remote", "--symref", "https://gitlab.com/acme/platform/web.git"], args);
        Assert.False(env.ContainsKey("GIT_CONFIG_COUNT"));
    }

    [Fact]
    public async Task A_private_projects_token_goes_in_a_header_for_gitlab_only_never_in_the_url()
    {
        await _settings.SetAsync(SettingKeys.GitLabUrl, "https://gitlab.example.com:8443");
        await _settings.SetSecretAsync(SettingKeys.GitLabToken, "glpat-secret");
        await _settings.SetSecretAsync(SettingKeys.GitHubToken, "ghp_secret");

        await _git.LsRemoteAsync(new GitLabRepoRef("acme", "web"));

        var (args, env) = Assert.Single(_ran);
        Assert.Equal("https://gitlab.example.com:8443/acme/web.git", args[^1]);
        Assert.DoesNotContain(args, a => a.Contains("glpat", StringComparison.Ordinal));
        Assert.Equal("http.https://gitlab.example.com:8443/.extraheader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("AUTHORIZATION: basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("oauth2:glpat-secret")), env["GIT_CONFIG_VALUE_0"]);

        // A GitHub repo's commands carry GitHub's header, as they always have.
        _ran.Clear();
        await _git.LsRemoteAsync(new GitHubRepoRef("acme", "web"));
        var (_, githubEnv) = Assert.Single(_ran);
        Assert.Equal("http.https://github.com/.extraheader", githubEnv["GIT_CONFIG_KEY_0"]);
    }

    [Fact]
    public async Task A_merge_requests_head_is_fetched_from_the_target_project()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gl-" + Guid.NewGuid().ToString("N"));
        try
        {
            await _git.FetchAsync(new GitLabRepoRef("acme", "web"), "refs/merge-requests/7/head", null, dir, null);

            Assert.Contains(_ran, r => r.Args.SequenceEqual(["fetch", "--depth", "1", "--no-tags", "https://gitlab.com/acme/web.git", "refs/merge-requests/7/head"]));
            // Only a merge request's head: never its merge ref, nor anything git could read as something else.
            await Assert.ThrowsAsync<GitException>(() => _git.FetchAsync(new GitLabRepoRef("acme", "web"), "refs/merge-requests/7/merge", null, dir, null));
        }
        finally
        {
            DeployPaths.TryDeleteDirectory(dir);
        }
    }
}
