using Gitberth.Detection;

namespace Gitberth.Tests;

public class NodeScriptsTests
{
    private static IReadOnlyList<string> Used(params (string Name, string Command)[] scripts) =>
        NodeScripts.SubPackagesUsedBy(scripts.ToDictionary(s => s.Name, s => s.Command), "build", "start");

    [Fact]
    public void Follows_npm_run_chains_and_hooks_into_a_sub_package()
    {
        // The layout of a repo whose root build also builds an admin app in its own folder.
        var used = Used(
            ("build", "npm run build:web && npm run build:admin"),
            ("prebuild:web", "npm run preflight"),
            ("preflight", "node scripts/check.mjs"),
            ("build:web", "vite build && npm run copy:tool"),
            ("copy:tool", "node scripts/copy-tool.mjs"),
            ("build:admin", "cd admin && npm run build"),
            ("dev", "cd website && npm run dev"));
        Assert.Equal(["admin"], used);
    }

    [Theory]
    [InlineData("npm --prefix admin run build", "admin")]
    [InlineData("npm run build --prefix=./admin/", "admin")]
    [InlineData("yarn --cwd packages/ui build", "packages/ui")]
    [InlineData("pnpm -C admin build", "admin")]
    [InlineData("pnpm --dir=admin run build", "admin")]
    [InlineData("cd admin; tsc -b && vite build", "admin")]
    [InlineData("(cd admin && npm run build) && vite build", "admin")]
    [InlineData("NODE_ENV=production cross-env FOO=1 npm --prefix admin run build", "admin")]
    [InlineData("concurrently \"npm --prefix api run build\" \"vite build\"", "api")]
    public void Finds_the_folder_a_command_runs_in(string build, string expected) =>
        Assert.Equal([expected], Used(("build", build)));

    [Theory]
    [InlineData("vite build")]
    [InlineData("cd admin && npm ci && npm run build")]   // installs for itself
    [InlineData("npm --prefix admin install && npm --prefix admin run build")]
    [InlineData("cd .. && npm run build")]                // outside the repo
    [InlineData("cd $APP_DIR && npm run build")]
    [InlineData("npm run build -w admin")]                // a workspace: the root install covers it
    [InlineData("(cd admin && npm ci) && cd admin && vite build")]
    public void Leaves_out_folders_it_should_not_install(string build) =>
        Assert.Empty(Used(("build", build)));

    [Fact]
    public void Expands_npm_run_all_globs_and_ignores_unrelated_scripts()
    {
        var used = Used(
            ("build", "run-s build:*"),
            ("build:web", "vite build"),
            ("build:api", "cd api && tsc"),
            ("lint", "cd tools && eslint ."));
        Assert.Equal(["api"], used);
    }

    [Fact]
    public void Survives_scripts_that_call_each_other() =>
        Assert.Equal(["admin"], Used(("build", "npm run a"), ("a", "npm run b && cd admin && vite build"), ("b", "npm run a")));

    [Theory]
    [InlineData("admin", new[] { "admin" }, true)]
    [InlineData("packages/ui", new[] { "packages/*" }, true)]
    [InlineData("apps/web/client", new[] { "./apps/**" }, true)]
    [InlineData("packages/ui/deep", new[] { "packages/*" }, false)]
    [InlineData("admin", new[] { "packages/*" }, false)]
    public void Matches_workspace_globs(string dir, string[] globs, bool expected) =>
        Assert.Equal(expected, NodeScripts.IsWorkspace(dir, globs));
}
