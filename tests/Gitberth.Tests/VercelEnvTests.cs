using Gitberth.Data;
using Gitberth.Deploy;
using Gitberth.Docker;
using Gitberth.Domains;

namespace Gitberth.Tests;

public class VercelEnvTests
{
    private static DeployFacts Facts(string? domain = "app.example.com", string? prefix = null, bool authJs = false) =>
        new(domain, "acme", "web", "main", "0123456789abcdef", "Fix the footer", "Ada", new AppTraits(prefix, authJs));

    private static Dictionary<string, string> Map(IEnumerable<EnvValue> env) => env.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public void Tells_the_app_where_it_runs_and_what_was_deployed_as_vercel_does()
    {
        var env = Map(VercelEnv.For(Facts()));

        Assert.Equal("production", env["VERCEL_ENV"]);
        Assert.Equal("production", env["VERCEL_TARGET_ENV"]);
        // Bare host names, as on Vercel: code adds https:// itself.
        Assert.Equal("app.example.com", env["VERCEL_URL"]);
        Assert.Equal("app.example.com", env["VERCEL_BRANCH_URL"]);
        Assert.Equal("app.example.com", env["VERCEL_PROJECT_PRODUCTION_URL"]);
        Assert.Equal("github", env["VERCEL_GIT_PROVIDER"]);
        Assert.Equal("acme", env["VERCEL_GIT_REPO_OWNER"]);
        Assert.Equal("web", env["VERCEL_GIT_REPO_SLUG"]);
        Assert.Equal("main", env["VERCEL_GIT_COMMIT_REF"]);
        Assert.Equal("0123456789abcdef", env["VERCEL_GIT_COMMIT_SHA"]);
        Assert.Equal("Fix the footer", env["VERCEL_GIT_COMMIT_MESSAGE"]);
        Assert.Equal("Ada", env["VERCEL_GIT_COMMIT_AUTHOR_NAME"]);
        // VERCEL=1 makes Next.js and others believe they run on Vercel's own platform.
        Assert.False(env.ContainsKey("VERCEL"));
        Assert.False(env.ContainsKey("AUTH_URL"));
        Assert.All(VercelEnv.For(Facts()), e => Assert.False(e.IsSecret));
    }

    [Fact]
    public void Without_a_domain_there_is_no_address_to_give()
    {
        var env = Map(VercelEnv.For(Facts(domain: null, authJs: true) with { CommitSha = null, CommitMessage = null, CommitAuthor = null }));

        Assert.DoesNotContain("VERCEL_URL", env.Keys);
        Assert.DoesNotContain("VERCEL_PROJECT_PRODUCTION_URL", env.Keys);
        Assert.DoesNotContain("VERCEL_GIT_COMMIT_SHA", env.Keys);
        Assert.DoesNotContain("AUTH_URL", env.Keys);
        Assert.Equal("production", env["VERCEL_ENV"]);
    }

    [Fact]
    public void Front_ends_get_copies_with_their_frameworks_prefix_and_auth_js_gets_its_address()
    {
        var env = Map(VercelEnv.For(Facts(prefix: "NEXT_PUBLIC_", authJs: true)));

        Assert.Equal("app.example.com", env["NEXT_PUBLIC_VERCEL_URL"]);
        Assert.Equal("0123456789abcdef", env["NEXT_PUBLIC_VERCEL_GIT_COMMIT_SHA"]);
        Assert.Equal("production", env["NEXT_PUBLIC_VERCEL_ENV"]);
        Assert.Equal("https://app.example.com", env["AUTH_URL"]);
        Assert.Equal("https://app.example.com", env["NEXTAUTH_URL"]);
        Assert.DoesNotContain("NEXT_PUBLIC_AUTH_URL", env.Keys);
    }

    [Fact]
    public void The_apps_own_values_win_and_auth_js_reads_its_two_names_as_one()
    {
        List<EnvValue> own = [new("VERCEL_ENV", "staging", false), new("NEXTAUTH_URL", "https://login.example.com", false)];

        var env = VercelEnv.AddTo(own, VercelEnv.For(Facts(authJs: true)));

        Assert.Single(env, e => e.Key == "VERCEL_ENV");
        Assert.Equal("staging", env.Single(e => e.Key == "VERCEL_ENV").Value);
        Assert.DoesNotContain(env, e => e.Key == "AUTH_URL");
        Assert.Equal("https://login.example.com", env.Single(e => e.Key == "NEXTAUTH_URL").Value);
        Assert.Contains(env, e => e.Key == "VERCEL_URL");
    }

    [Fact]
    public void Finds_the_framework_prefix_and_auth_js_from_the_apps_files()
    {
        using var repo = new TempRepo()
            .With("site/package.json", """{ "dependencies": { "next": "16.0.0", "react": "19.0.0", "next-auth": "5.0.0" } }""")
            .With("spa/package.json", """{ "devDependencies": { "vite": "7.0.0" }, "dependencies": { "@auth0/auth0-react": "2.0.0" } }""")
            .With("astro/package.json", """{ "dependencies": { "astro": "5.0.0", "@auth/core": "0.40.0" } }""")
            .With("preset/package.json", """{ "dependencies": { "vite": "7.0.0" } }""")
            .With("preset/vercel.json", """{ "framework": "create-react-app" }""")
            .With("api/package.json", """{ "dependencies": { "express": "5.0.0" } }""")
            .With("monorepo/apps/web/index.ts", "");
        string In(string folder) => Path.Combine(repo.Root, folder);

        Assert.Equal(new AppTraits("NEXT_PUBLIC_", true), VercelEnv.Detect(In("site"), repo.Root));
        // @auth0 is Auth0's SDK, not Auth.js.
        Assert.Equal(new AppTraits("VITE_", false), VercelEnv.Detect(In("spa"), repo.Root));
        Assert.Equal(new AppTraits("PUBLIC_", true), VercelEnv.Detect(In("astro"), repo.Root));
        Assert.Equal(new AppTraits("REACT_APP_", false), VercelEnv.Detect(In("preset"), repo.Root));
        Assert.Equal(AppTraits.None, VercelEnv.Detect(In("api"), repo.Root));
        // A folder with no package.json of its own goes by the one at the repo root.
        Assert.Equal(new AppTraits("NEXT_PUBLIC_", true), VercelEnv.Detect(In("monorepo/apps/web"), In("site")));
    }

    [Fact]
    public void What_a_build_found_travels_on_the_image()
    {
        var traits = new AppTraits("NEXT_PUBLIC_", true);
        var args = traits.LabelArgs().ToList();
        var labels = new Dictionary<string, string>();
        for (var i = 0; i < args.Count; i += 2)
        {
            Assert.Equal("--label", args[i]);
            var (key, value) = (args[i + 1].Split('=')[0], args[i + 1][(args[i + 1].IndexOf('=') + 1)..]);
            labels[key] = value;
        }

        Assert.Equal(traits, AppTraits.FromLabels(labels));
        Assert.Equal(AppTraits.None, AppTraits.FromLabels(new Dictionary<string, string> { ["gitberth.traits"] = "1" }));
        // An image built before Gitberth kept these says nothing either way.
        Assert.Null(AppTraits.FromLabels(new Dictionary<string, string> { ["gitberth.app"] = "web" }));
    }

    [Fact]
    public void The_deploy_log_says_what_the_app_was_told()
    {
        var lines = DeployPipeline.DescribeVercelVariables([], Facts(prefix: "NEXT_PUBLIC_", authJs: true)).ToList();
        Assert.Contains("VERCEL_URL=app.example.com and VERCEL_ENV=production, also as NEXT_PUBLIC_VERCEL_…", lines[0]);
        Assert.Contains("AUTH_URL and NEXTAUTH_URL are https://app.example.com", lines[1]);

        var preview = DeployPipeline.DescribeVercelVariables([], Facts() with { Production = false, Branch = "staging" }).Single();
        Assert.Contains("VERCEL_ENV=preview, because staging isn't the repo's default branch", preview);

        var checks = DeployPipeline.DescribeVercelVariables([], Facts() with { Traits = new AppTraits(null, false, ChecksVercel: true) }).Single();
        Assert.Contains("VERCEL_PROJECT_PRODUCTION_URL=app.example.com", checks);
        Assert.Contains("checks process.env.VERCEL", checks);

        var local = DeployPipeline.DescribeVercelVariables([new("NEXTAUTH_URL", "http://localhost:3000", false)], Facts(authJs: true)).ToList();
        Assert.StartsWith("warning: NEXTAUTH_URL is http://localhost:3000", local[1]);
        Assert.Equal(2, local.Count);

        var noDomain = DeployPipeline.DescribeVercelVariables([], Facts(domain: null, authJs: true)).ToList();
        Assert.Contains("VERCEL_URL isn't", noDomain[0]);
        Assert.StartsWith("warning:", noDomain[1]);

        Assert.Single(DeployPipeline.DescribeVercelVariables([new("AUTH_URL", "https://x.example.com", false)], Facts(authJs: true)));
    }

    [Fact]
    public void Lists_every_arg_a_dockerfile_declares() =>
        Assert.Equal(["BASE", "NODE_VERSION", "VERCEL_URL", "X"],
            DeployPipeline.DeclaredArgs("ARG BASE=node\nFROM ${BASE}\narg NODE_VERSION\nRUN echo\n  ARG VERCEL_URL X=1\n").Order());

    [Fact]
    public void A_build_gets_neither_the_bare_vercel_env_nor_auth_js_s_address()
    {
        var build = Map(VercelEnv.ForBuild(Facts(prefix: "NUXT_ENV_", authJs: true)));

        // Nitro and std-env take a bare VERCEL_ENV to mean the build is for Vercel, and a Nuxt build then writes .vercel/output.
        Assert.DoesNotContain("VERCEL_ENV", build.Keys);
        Assert.Equal("production", build["NUXT_ENV_VERCEL_ENV"]);
        Assert.DoesNotContain("AUTH_URL", build.Keys);
        Assert.DoesNotContain("NEXTAUTH_URL", build.Keys);
        Assert.Equal("app.example.com", build["VERCEL_URL"]);
        Assert.Equal("production", Map(VercelEnv.For(Facts()))["VERCEL_ENV"]);
    }

    [Fact]
    public void A_branch_other_than_the_default_one_is_a_preview()
    {
        var env = Map(VercelEnv.For(Facts() with { Production = false }));

        Assert.Equal("preview", env["VERCEL_ENV"]);
        Assert.Equal("preview", env["VERCEL_TARGET_ENV"]);
        Assert.Equal("app.example.com", env["VERCEL_PROJECT_PRODUCTION_URL"]);
    }

    [Fact]
    public void Code_that_checks_process_env_vercel_gets_no_bare_vercel_url()
    {
        using var repo = new TempRepo()
            .With("t3/package.json", """{ "dependencies": { "next": "15.0.0", "next-auth": "4.24.0", "@t3-oss/env-nextjs": "0.11.0" } }""")
            .With("t3/src/env.js", """export const env = createEnv({ server: { NEXTAUTH_URL: z.preprocess((s) => process.env.VERCEL_URL ?? s, process.env.VERCEL ? z.string() : z.string().url()) } });""")
            .With("plain/package.json", """{ "dependencies": { "next": "15.0.0" } }""")
            .With("plain/app/page.tsx", """export const url = `https://${process.env.VERCEL_URL}`;""");

        var t3 = VercelEnv.Detect(Path.Combine(repo.Root, "t3"), repo.Root);
        Assert.True(t3.ChecksVercel);
        var env = Map(VercelEnv.For(Facts(prefix: t3.Prefix, authJs: t3.AuthJs) with { Traits = t3 }));
        Assert.DoesNotContain("VERCEL_URL", env.Keys);
        Assert.DoesNotContain("VERCEL_BRANCH_URL", env.Keys);
        Assert.Equal("app.example.com", env["VERCEL_PROJECT_PRODUCTION_URL"]);
        Assert.Equal("https://app.example.com", env["NEXTAUTH_URL"]);

        Assert.False(VercelEnv.Detect(Path.Combine(repo.Root, "plain"), repo.Root).ChecksVercel);
    }

    [Fact]
    public void Finds_auth_js_in_a_workspace_package_the_app_depends_on()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "name": "acme", "private": true, "workspaces": ["apps/*", "packages/*"] }""")
            .With("apps/web/package.json", """{ "name": "@acme/web", "dependencies": { "next": "15.0.0", "@acme/auth": "workspace:*" } }""")
            .With("packages/auth/package.json", """{ "name": "@acme/auth", "dependencies": { "next-auth": "5.0.0" } }""")
            .With("apps/docs/package.json", """{ "name": "@acme/docs", "dependencies": { "next": "15.0.0", "@acme/ui": "workspace:*" } }""")
            .With("packages/ui/package.json", """{ "name": "@acme/ui", "dependencies": { "react": "19.0.0" } }""")
            .With("apps/local/package.json", """{ "name": "local", "dependencies": { "auth": "file:../../packages/auth" } }""");

        Assert.True(VercelEnv.Detect(Path.Combine(repo.Root, "apps", "web"), repo.Root).AuthJs);
        Assert.False(VercelEnv.Detect(Path.Combine(repo.Root, "apps", "docs"), repo.Root).AuthJs);
        Assert.True(VercelEnv.Detect(Path.Combine(repo.Root, "apps", "local"), repo.Root).AuthJs);
    }

    [Fact]
    public void A_value_the_app_sets_for_when_it_runs_wins_during_the_build_too() =>
        Assert.DoesNotContain(VercelEnv.AddTo([], VercelEnv.ForBuild(Facts()), alsoSet: ["VERCEL_PROJECT_PRODUCTION_URL"]), e => e.Key == "VERCEL_PROJECT_PRODUCTION_URL");
}

public class MainDomainTests
{
    private static CustomDomain D(int id, string hostname, bool main = false) => new() { Id = id, Hostname = hostname, Main = main };

    [Fact]
    public void The_shortest_domain_is_the_main_one_unless_another_is_chosen()
    {
        Assert.Null(DomainService.MainOf([]));
        // Vercel picks a project's production URL the same way.
        Assert.Equal("harborlab.com", DomainService.MainOf([D(1, "www.harborlab.com"), D(2, "harborlab.com"), D(3, "app.harborlab.com")]));
        // Of two the same length, the one added first.
        Assert.Equal("www.example.com", DomainService.MainOf([D(2, "app.example.com"), D(1, "www.example.com")]));
        Assert.Equal("app.harborlab.com", DomainService.MainOf([D(1, "harborlab.com"), D(2, "app.harborlab.com", main: true)]));
        // One being added (no ID yet) comes last, as it will once it's saved.
        Assert.Equal("www.example.com", DomainService.MainOf([D(0, "app.example.com"), D(5, "www.example.com")]));
    }

    [Theory]
    [InlineData("2026-11-04T02:55:45Z INF Updated to new configuration config=\"{\\\"ingress\\\":[]}\" version=14", 14, true)]
    [InlineData("2026-11-04T02:55:45Z INF Updated to new configuration config=\"{}\" version=14", 13, true)]
    [InlineData("2026-11-04T02:55:45Z INF Updated to new configuration config=\"{}\" version=13", 14, false)]
    [InlineData("2026-11-04T02:55:45Z INF Registered tunnel connection connIndex=0 version=2026.10.0", 1, false)]
    [InlineData("\u001b[90m2026-11-04T02:55:45Z\u001b[0m \u001b[32mINF\u001b[0m Updated to new configuration \u001b[36mconfig=\u001b[0m\"{}\" \u001b[36mversion=\u001b[0m15", 15, true)]
    [InlineData("{\"level\":\"info\",\"version\":16,\"time\":\"2026-11-04T02:55:45Z\",\"message\":\"Updated to new configuration\"}", 16, true)]
    public void Reads_which_tunnel_configuration_cloudflared_runs(string line, long version, bool runs) =>
        Assert.Equal(runs, DomainService.RunsVersion("2026-11-04T02:55:40Z INF Starting tunnel\n" + line + "\n", version));

}