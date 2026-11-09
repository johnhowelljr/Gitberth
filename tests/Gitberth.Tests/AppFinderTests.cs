using Gitberth.Detection;

namespace Gitberth.Tests;

public class AppFinderTests
{
    // A repo shaped like a product with a hosted site: a Vite tool at the root whose build is copied into a
    // Next.js website, an admin SPA, a shared server package, an extension, and Dockerfiles for self-hosting.
    private static TempRepo ProductRepo() => new TempRepo()
        .With(".nvmrc", "24")
        .With("package.json", """{ "name": "product", "scripts": { "build": "npm run build:web && npm run build:admin", "build:web": "vite build", "build:admin": "cd admin && npm run build" }, "dependencies": { "react": "^19.0.0" }, "devDependencies": { "vite": "^7.0.0" } }""")
        .With("package-lock.json", "{}")
        .With("index.html", "<div id=root></div>")
        .With("vercel.json", """{ "rootDirectory": "website", "installCommand": "cd .. && npm ci", "buildCommand": "cd .. && npm run build && cd website && npm run build", "framework": "nextjs" }""")
        .With("Dockerfile.e2e-appliance", "FROM node:24-slim\nCOPY . .\nEXPOSE 3000\n")
        .With("website/package.json", """{ "scripts": { "build": "next build", "start": "next start" }, "dependencies": { "next": "16.0.0", "react": "19.0.0", "server": "file:../server" } }""")
        .With("website/package-lock.json", "{}")
        .With("website/vercel.json", """{ "installCommand": "cd .. && npm ci && cd server && npm ci && cd ../admin && npm ci && cd ../website && npm ci", "buildCommand": "cd .. && npm run build:web && npm run build:admin && cd website && npm run build", "framework": "nextjs" }""")
        .With("website/next.config.ts", "export default { ...(process.env.STANDALONE ? { output: 'standalone' } : {}) };")
        .With("admin/package.json", """{ "scripts": { "build": "tsc -b && vite build" }, "dependencies": { "react-router-dom": "^7.0.0" }, "devDependencies": { "vite": "^7.0.0" } }""")
        .With("server/package.json", """{ "name": "server", "main": "src/index.ts", "scripts": { "typecheck": "tsc --noEmit" } }""")
        .With("server/src/index.ts", "export {};")
        .With("extensions/capture/package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
        .With("docker/enterprise/Dockerfile", "FROM node:24-slim\nWORKDIR /app\nCOPY package.json package-lock.json ./\nCOPY website/ website/\nCOPY --from=node:24 /usr/local/bin/node /usr/local/bin/\nEXPOSE 3000\n")
        .With("tests/fixtures/app/package.json", """{ "scripts": { "start": "node index.js" } }""");

    [Fact]
    public void Finds_every_app_and_ranks_the_one_the_vercel_config_points_at_first()
    {
        using var repo = ProductRepo();
        var apps = new ProjectAnalyzer().FindApps(repo.Root);

        Assert.Equal("website", apps[0].Dir);
        Assert.Equal(ProjectKind.NextJs, apps[0].Kind);
        Assert.Contains("vercel.json points here", apps[0].Detail);

        var dirs = apps.Select(a => (a.Dir, a.Hint)).ToList();
        Assert.Contains(("", (string?)null), dirs);                     // the Vite tool at the root
        Assert.Contains(("admin", (string?)null), dirs);
        Assert.Contains(("docker/enterprise", (string?)null), dirs);
        Assert.Contains(("", "Dockerfile.e2e-appliance"), dirs);
        Assert.DoesNotContain(apps, a => a.Dir == "server");          // a library, not something to run
        Assert.DoesNotContain(apps, a => a.Dir.StartsWith("tests")); // test fixtures are never offered

        // Helpers rank below the real apps.
        int Rank(string dir, string? hint = null) => apps.FindIndex(a => a.Dir == dir && a.Hint == hint);
        Assert.True(Rank("") < Rank("admin"));
        Assert.True(Rank("docker/enterprise") < Rank("", "Dockerfile.e2e-appliance"));
        Assert.True(Rank("admin") < Rank("extensions/capture"));
    }

    [Fact]
    public void A_sub_folder_app_with_a_vercel_config_is_built_the_way_vercel_builds_it()
    {
        using var repo = ProductRepo();
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "website"));

        Assert.Equal(ProjectKind.NextJs, r.Kind);
        Assert.Equal("", r.BuildContext); // the whole repo, so `cd ..` works
        Assert.Contains("FROM node:24-slim", r.Dockerfile);      // from the root's .nvmrc
        Assert.StartsWith("# gitberth:context repo-root\n", r.Dockerfile);
        Assert.Contains("""
            COPY . .
            # gitberth:build-args
            WORKDIR /app/website
            ENV PATH=/app/website/node_modules/.bin:/app/node_modules/.bin:$PATH
            RUN --mount=type=cache,id=npm,target=/root/.npm cd .. && npm ci && cd server && npm ci && cd ../admin && npm ci && cd ../website && npm ci
            RUN cd .. && npm run build:web && npm run build:admin && cd website && npm run build
            """.ReplaceLineEndings("\n"), r.Dockerfile);
        Assert.Contains("CMD [\"npm\", \"start\"]", r.Dockerfile); // next start, not the standalone server
        Assert.DoesNotContain(".next/standalone", r.Dockerfile);
    }

    [Fact]
    public void A_sub_folder_app_gets_the_installs_its_scripts_and_local_dependencies_need()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "tokens": "node build-tokens.mjs" }, "devDependencies": { "style-dictionary": "^4.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("apps/web/package.json", """{ "scripts": { "build": "cd ../.. && npm run tokens && cd apps/web && vite build" }, "dependencies": { "shared": "file:../../packages/shared" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("apps/web/package-lock.json", "{}")
            .With("packages/shared/package.json", """{ "name": "shared", "dependencies": { "zod": "^4.0.0" } }""");
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "apps/web"));

        Assert.Equal(ProjectKind.NodeSpa, r.Kind);
        Assert.Equal("", r.BuildContext);
        Assert.Contains("""
            WORKDIR /app/apps/web
            RUN --mount=type=cache,id=npm,target=/root/.npm cd ../.. && npm ci
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN --mount=type=cache,id=npm,target=/root/.npm cd ../../packages/shared && npm install
            RUN npm run build
            """.ReplaceLineEndings("\n"), r.Dockerfile);
        Assert.Contains("COPY --from=build /app/apps/web/dist/ /usr/share/nginx/html/", r.Dockerfile);
    }

    [Fact]
    public void A_workspace_member_is_installed_by_the_workspace_root()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "workspaces": ["apps/*", "packages/*"] }""")
            .With("package-lock.json", "{}")
            .With("apps/api/package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("apps/api/server.js", "require('express')().listen(process.env.PORT || 4000)");
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "apps/api"));

        Assert.Equal(ProjectKind.NodeServer, r.Kind);
        Assert.Equal(4000, r.ContainerPort);
        Assert.Contains("WORKDIR /app/apps/api\nRUN --mount=type=cache,id=npm,target=/root/.npm cd ../.. && npm ci\nENV", r.Dockerfile);
        Assert.DoesNotContain("RUN --mount=type=cache,id=npm,target=/root/.npm npm ci\n", r.Dockerfile);
    }

    [Fact]
    public void A_dockerfile_in_a_sub_folder_builds_from_wherever_its_copy_lines_point()
    {
        using var repo = ProductRepo()
            .With("services/api/Dockerfile", "FROM node:24-slim\nCOPY package.json ./\nCOPY src/ src/\nEXPOSE 8080\n")
            .With("services/api/package.json", "{}")
            .With("services/api/src/index.js", "");
        var analyzer = new ProjectAnalyzer();

        var enterprise = analyzer.Analyze(repo.Root, new AnalyzeOptions(AppDir: "docker/enterprise"));
        Assert.Equal(ProjectKind.Dockerfile, enterprise.Kind);
        Assert.Equal("docker/enterprise/Dockerfile", enterprise.RepoDockerfile);
        Assert.Equal("", enterprise.BuildContext); // COPY website/ only makes sense from the root

        var api = analyzer.Analyze(repo.Root, new AnalyzeOptions(AppDir: "services/api"));
        Assert.Equal("services/api", api.BuildContext);
        Assert.Equal(8080, api.ContainerPort);

        var variant = analyzer.Analyze(repo.Root, new AnalyzeOptions("Dockerfile.e2e-appliance"));
        Assert.Equal("Dockerfile.e2e-appliance", variant.RepoDockerfile);
    }

    [Fact]
    public void Reads_copy_sources()
    {
        const string dockerfile = """
            FROM node:24 AS build
            COPY package.json package-lock.json ./
            COPY --chown=node:node ["src/app", "/app/src"]
            COPY --from=build /app/dist /usr/share/nginx/html
            ADD https://example.com/file.tgz /tmp/
            COPY apps/*/package.json \
                 ./
            COPY ./.npmrc ./
            COPY <<EOF /etc/conf
            EOF
            """;
        Assert.Equal(["package.json", "package-lock.json", "src/app", "apps/*/package.json", ".npmrc"], ProjectAnalyzer.CopySources(dockerfile));
    }

    [Fact]
    public void A_dockerfile_that_only_copies_everything_builds_from_the_root_unless_its_folder_is_a_project()
    {
        using var repo = new TempRepo()
            .With("package.json", "{}")
            .With("docker/hosted/Dockerfile", "FROM node:24-slim\nCOPY . .\nEXPOSE 3000\n")
            .With("services/api/Dockerfile", "FROM node:24-slim\nCOPY . .\nEXPOSE 8080\n")
            .With("services/api/package.json", "{}")
            .With("tools/Dockerfile", "FROM alpine\nCOPY package*.json ./\nCOPY .npmrc ./\n")
            .With(".npmrc", "");
        Assert.Equal("", ProjectAnalyzer.DockerContext(repo.Root, "docker/hosted", "COPY . ."));
        Assert.Equal("services/api", ProjectAnalyzer.DockerContext(repo.Root, "services/api", "COPY . ."));
        // package*.json and .npmrc only exist at the root.
        Assert.Equal("", ProjectAnalyzer.DockerContext(repo.Root, "tools", "COPY package*.json ./\nCOPY .npmrc ./"));
    }

    [Theory]
    [InlineData("./web/", "web")]
    [InlineData("web\\api", "web/api")]
    [InlineData(" my app ", "my app")]
    [InlineData("apps/../web", "web")]
    [InlineData("../outside", null)]
    [InlineData("", "")]
    public void Normalizes_the_subfolder(string input, string? expected) =>
        Assert.Equal(expected, ProjectAnalyzer.NormalizeDir(input));

    [Fact]
    public void A_folder_name_with_a_space_is_built_from_that_folder_not_the_repo_root()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("my app/package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("my app/server.js", "");
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "my app"));

        Assert.Equal(ProjectKind.NodeServer, r.Kind); // the folder's app, not the Vite app at the root
        Assert.Equal("my app", r.BuildContext);
        Assert.DoesNotContain("WORKDIR /app/", r.Dockerfile);
    }

    [Fact]
    public void A_pnpm_workspace_member_installs_from_the_root_and_runs_with_pnpm()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "packageManager": "pnpm@10.0.0" }""")
            .With("pnpm-lock.yaml", "lockfileVersion: '9.0'")
            .With("pnpm-workspace.yaml", "packages:\n  - 'apps/*'\n  - \"packages/*\"\n  - '!**/test/**'\nonlyBuiltDependencies:\n  - esbuild\n")
            .With("apps/web/package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("tools/site/package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        var analyzer = new ProjectAnalyzer();

        var web = analyzer.Analyze(repo.Root, new AnalyzeOptions(AppDir: "apps/web"));
        Assert.Contains("RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store cd ../.. && pnpm install --frozen-lockfile\nRUN pnpm run build", web.Dockerfile);
        Assert.Contains("corepack enable", web.Dockerfile);

        // Not listed in pnpm-workspace.yaml: an ordinary folder with its own install.
        var site = analyzer.Analyze(repo.Root, new AnalyzeOptions(AppDir: "tools/site"));
        Assert.Contains("RUN --mount=type=cache,id=npm,target=/root/.npm npm install\nRUN npm run build", site.Dockerfile);
        Assert.Equal(["apps/*", "packages/*"], ProjectAnalyzer.PnpmWorkspaceGlobs(repo.Root));
    }

    [Fact]
    public void A_bare_vercel_build_command_finds_its_tools_like_on_vercel()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "next build", "start": "next start" }, "dependencies": { "next": "16.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("vercel.json", """{ "buildCommand": "prisma generate && next build" }""");
        var r = new ProjectAnalyzer().Analyze(repo.Root);
        Assert.Contains("ENV PATH=/app/node_modules/.bin:$PATH\nRUN --mount=type=cache,id=npm,target=/root/.npm npm ci\nRUN prisma generate && next build", r.Dockerfile);
    }

    [Theory]
    [InlineData("", "/app/node_modules/.bin")]
    [InlineData("apps/web", "/app/apps/web/node_modules/.bin:/app/apps/node_modules/.bin:/app/node_modules/.bin")]
    public void Puts_every_node_modules_bin_up_to_the_root_on_path(string appDir, string expected) =>
        Assert.Equal(expected, DockerfileTemplates.BinPath(appDir));

    [Theory]
    [InlineData("postgres://kanban:s3cret@postgres:5432/kanban", true)]
    [InlineData("redis://default:pw@cache:6379", true)]
    [InlineData("https://harborlab.com", false)]
    [InlineData("postgres://postgres/kanban", false)]
    public void Spots_urls_with_passwords(string value, bool expected) =>
        Assert.Equal(expected, EnvRules.HasCredentials(value));

    [Fact]
    public void The_node_hint_picks_the_node_app_over_a_dockerfile_in_the_same_folder()
    {
        using var repo = new TempRepo()
            .With("Dockerfile", "FROM alpine\nEXPOSE 80\n")
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "");
        var analyzer = new ProjectAnalyzer();
        Assert.Equal(ProjectKind.Dockerfile, analyzer.Analyze(repo.Root).Kind);
        Assert.Equal(ProjectKind.NodeServer, analyzer.Analyze(repo.Root, new AnalyzeOptions(ProjectAnalyzer.NodeHint)).Kind);
    }

    [Theory]
    [InlineData("", "admin", "admin")]
    [InlineData("website", "", "..")]
    [InlineData("website", "server", "../server")]
    [InlineData("apps/web", "packages/ui", "../../packages/ui")]
    [InlineData("apps/web", "apps/web", ".")]
    public void Writes_relative_paths_for_cd(string from, string to, string expected) =>
        Assert.Equal(expected, NodeScripts.Relative(from, to));

    [Fact]
    public void A_root_vercel_config_that_points_elsewhere_belongs_to_that_folder()
    {
        using var repo = ProductRepo();
        Assert.Equal("website", VercelConfig.MainFolder(repo.Root));
        Assert.Null(VercelConfig.Load(repo.Root));
        Assert.StartsWith("cd .. && npm ci", VercelConfig.Load(Path.Combine(repo.Root, "website"))!.InstallCommand);

        // So the root app keeps its own build instead of running commands meant for website/.
        var root = new ProjectAnalyzer().Analyze(repo.Root);
        Assert.Equal("Vite + React", root.Title);
        Assert.Contains("RUN npm run build\n", root.Dockerfile);
    }
}
