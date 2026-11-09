using Gitberth.Detection;

namespace Gitberth.Tests;

public class ProjectAnalyzerTests
{
    private const string WebNet10 = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    private static DetectionResult Analyze(TempRepo repo, string? hint = null) => new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint));

    [Fact]
    public void Static_site_is_served_by_nginx()
    {
        using var repo = new TempRepo().With("index.html", "<h1>hi</h1>");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Static, r.Kind);
        Assert.Equal(80, r.ContainerPort);
        Assert.Contains("FROM nginx:stable-alpine", r.Dockerfile);
        Assert.Contains("COPY ./ /usr/share/nginx/html/", r.Dockerfile);
    }

    [Fact]
    public void Static_site_in_public_folder()
    {
        using var repo = new TempRepo().With("public/index.html", "x").With("README.md", "docs");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Static, r.Kind);
        Assert.Contains("COPY public/ /usr/share/nginx/html/", r.Dockerfile);
    }

    [Fact]
    public void Repo_dockerfile_wins()
    {
        using var repo = new TempRepo().With("Dockerfile", "FROM node:24\nEXPOSE 5000\n").With("package.json", "{}").With("index.html", "x");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Dockerfile, r.Kind);
        Assert.Equal(5000, r.ContainerPort);
        Assert.Equal("Dockerfile", r.RepoDockerfile);
        Assert.Null(r.Dockerfile);
    }

    [Fact]
    public void AspNetCore_project_skips_tests_and_suggests_env()
    {
        using var repo = new TempRepo()
            .With("MyApp.slnx", "<Solution />")
            .With("src/MyApp.Web/MyApp.Web.csproj", WebNet10)
            .With("src/MyApp.Web/appsettings.json", """{ "ConnectionStrings": { "Default": "Data Source=app.db" }, "Logging": { "LogLevel": { "Default": "Information" } }, "Stripe": { "ApiKey": "" } }""")
            .With("tests/MyApp.Web.Tests/MyApp.Web.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.9.3" /></ItemGroup></Project>""");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.DotNetWeb, r.Kind);
        Assert.Equal(8080, r.ContainerPort);
        Assert.Equal("src/MyApp.Web/MyApp.Web.csproj", r.SelectedProject);
        Assert.Single(r.ProjectCandidates);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", r.Dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0", r.Dockerfile);
        Assert.Contains("dotnet publish \"src/MyApp.Web/MyApp.Web.csproj\"", r.Dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"MyApp.Web.dll\"]", r.Dockerfile);
        Assert.Contains("USER $APP_UID", r.Dockerfile);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("end of support"));

        var env = r.EnvSuggestions.ToDictionary(s => s.Key);
        Assert.Equal("true", env["ASPNETCORE_FORWARDEDHEADERS_ENABLED"].Value);
        Assert.True(env["ConnectionStrings__Default"].IsSecret);
        Assert.Equal("", env["ConnectionStrings__Default"].Value);
        Assert.True(env["Stripe__ApiKey"].IsSecret);
        Assert.DoesNotContain("Logging__LogLevel__Default", env.Keys);
    }

    [Fact]
    public void Net8_warns_and_honors_assembly_name()
    {
        using var repo = new TempRepo()
            .With("Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>Acme.Api</AssemblyName></PropertyGroup></Project>""");
        var r = Analyze(repo);
        Assert.Contains("sdk:8.0", r.Dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Acme.Api.dll\"]", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("Nov 10, 2026"));
    }

    [Fact]
    public void Target_framework_from_directory_build_props_for_worker()
    {
        using var repo = new TempRepo()
            .With("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>")
            .With("src/Svc/Svc.csproj", """<Project Sdk="Microsoft.NET.Sdk.Worker"></Project>""");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.DotNetWorker, r.Kind);
        Assert.Null(r.ContainerPort);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime:9.0", r.Dockerfile);
    }

    [Fact]
    public void Aspire_host_is_skipped_and_hint_picks_project()
    {
        using var repo = new TempRepo()
            .With("AppHost/AppHost.csproj", """<Project Sdk="Microsoft.NET.Sdk"><Sdk Name="Aspire.AppHost.Sdk" Version="9.0.0" /><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><IsAspireHost>true</IsAspireHost></PropertyGroup></Project>""")
            .With("Api/Api.csproj", WebNet10)
            .With("Admin/Admin.csproj", WebNet10);

        var first = Analyze(repo);
        Assert.Equal(["Admin/Admin.csproj", "Api/Api.csproj"], first.ProjectCandidates);
        Assert.Equal("Admin/Admin.csproj", first.SelectedProject);
        Assert.Contains(first.Warnings, w => w.Contains("2 runnable projects"));

        var hinted = Analyze(repo, "Api/Api.csproj");
        Assert.Equal("Api/Api.csproj", hinted.SelectedProject);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Api.dll\"]", hinted.Dockerfile);
    }

    [Fact]
    public void Blazor_web_app_runs_the_server_project()
    {
        using var repo = new TempRepo()
            .With("Shop/Shop.csproj", WebNet10)
            .With("Shop.Client/Shop.Client.csproj", """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.DotNetWeb, r.Kind);
        Assert.Equal("Shop/Shop.csproj", r.SelectedProject);
    }

    [Fact]
    public void Net_framework_is_unsupported()
    {
        using var repo = new TempRepo()
            .With("Legacy.csproj", """<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><OutputType>Exe</OutputType><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup></Project>""");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Unknown, r.Kind);
        Assert.Contains(r.Warnings, w => w.Contains(".NET Framework"));
    }

    [Fact]
    public void Vite_react_spa_builds_to_nginx_with_build_time_env()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "dev": "vite", "build": "vite build" }, "dependencies": { "react": "^19.0.0" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("package-lock.json", "{}")
            .With(".env.example", "VITE_API_URL=https://api.example.com\nSECRET_TOKEN=changeme\n")
            .With("src/main.tsx", "console.log(import.meta.env.VITE_API_URL, import.meta.env.MODE, import.meta.env.VITE_FLAG)");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeSpa, r.Kind);
        Assert.Equal("Vite + React", r.Title);
        Assert.Equal(80, r.ContainerPort);
        Assert.Contains("FROM node:24-slim AS build", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=npm,target=/root/.npm npm ci", r.Dockerfile);
        Assert.Contains("RUN npm run build", r.Dockerfile);
        Assert.Contains("COPY --from=build /app/dist/ /usr/share/nginx/html/", r.Dockerfile);
        Assert.Contains("try_files $uri $uri/ /index.html;", r.Dockerfile);

        var env = r.EnvSuggestions.ToDictionary(s => s.Key);
        Assert.True(env["VITE_API_URL"].BuildTime);
        Assert.Equal("https://api.example.com", env["VITE_API_URL"].Value);
        Assert.True(env["SECRET_TOKEN"].IsSecret);
        Assert.Equal("", env["SECRET_TOKEN"].Value);
        Assert.True(env["VITE_FLAG"].BuildTime);
        Assert.DoesNotContain("MODE", env.Keys);
    }

    [Fact]
    public void Sub_packages_the_build_works_in_get_their_own_install()
    {
        // The root build also builds an admin app that has its own package.json and lockfile.
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "npm run build:web && npm run build:admin", "build:web": "vite build", "build:admin": "cd admin && npm run build" }, "dependencies": { "react": "^19.0.0" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("admin/package.json", """{ "scripts": { "build": "tsc -b && vite build" }, "dependencies": { "react-router-dom": "^7.0.0" } }""")
            .With("admin/package-lock.json", "{}")
            .With("website/package.json", """{ "scripts": { "build": "next build" } }""");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeSpa, r.Kind);
        Assert.Contains("""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN --mount=type=cache,id=npm,target=/root/.npm cd admin && npm ci
            RUN npm run build
            """.ReplaceLineEndings("\n"), r.Dockerfile);
        Assert.DoesNotContain("website", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.Contains("admin/"));
    }

    [Fact]
    public void Workspace_members_are_left_to_the_root_install()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "workspaces": ["admin"], "scripts": { "build": "vite build && cd admin && npm run build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("admin/package.json", """{ "scripts": { "build": "vite build" } }""");
        var r = Analyze(repo);
        Assert.DoesNotContain("cd admin", r.Dockerfile);
    }

    [Fact]
    public void Next_standalone_with_pnpm_and_nvmrc()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "next build", "start": "next start" }, "dependencies": { "next": "16.0.0", "react": "19.0.0" } }""")
            .With("pnpm-lock.yaml", "lockfileVersion: '9.0'")
            .With(".nvmrc", "22")
            .With("next.config.mjs", "export default { output: 'standalone' };")
            .With("public/favicon.ico", "x");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NextJs, r.Kind);
        Assert.Equal("Next.js (standalone)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("FROM node:22-slim AS build", r.Dockerfile);
        Assert.Contains("npm install -g corepack@latest && corepack enable", r.Dockerfile);
        Assert.Contains("pnpm install --frozen-lockfile", r.Dockerfile);
        Assert.Contains("COPY --from=build /app/public ./public", r.Dockerfile);
        Assert.Contains("HOSTNAME=0.0.0.0", r.Dockerfile);
        Assert.Contains("CMD [\"node\", \"server.js\"]", r.Dockerfile);
    }

    [Fact]
    public void Express_server_port_is_detected()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "main": "server.js", "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "const port = process.env.PORT || 4000;\napp.listen(port);\nconst key = process.env.STRIPE_KEY;");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeServer, r.Kind);
        Assert.Equal("Node.js server (Express)", r.Title);
        Assert.Equal(4000, r.ContainerPort);
        Assert.Contains("ENV NODE_ENV=production HOST=0.0.0.0 PORT=4000", r.Dockerfile);
        Assert.Contains("CMD [\"npm\", \"start\"]", r.Dockerfile);
        Assert.Contains("npm install", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("lockfile"));
        Assert.Contains(r.EnvSuggestions, s => s.Key == "STRIPE_KEY" && s.IsSecret && !s.BuildTime);
    }

    [Fact]
    public void Nothing_recognisable_is_unknown()
    {
        using var repo = new TempRepo().With("README.md", "hello");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Unknown, r.Kind);
        Assert.False(r.CanBuild);
    }

    [Fact]
    public void SvelteKit_adapter_auto_builds_with_adapter_node()
    {
        using var repo = SvelteKitApp("adapter-auto").With("package-lock.json", "{}");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.SvelteKit, r.Kind);
        Assert.Equal("SvelteKit (adapter-auto → adapter-node)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("Built with @sveltejs/adapter-node instead of adapter-auto, without changing the repo", r.Evidence);
        Assert.Empty(r.Warnings);
        // Installed after the app's own dependencies and swapped into the config; then the app builds and runs as usual.
        Assert.Contains(Lf($"""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install -D @sveltejs/adapter-node@5
            {ProjectAnalyzer.SvelteKitNodeSwap}
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["node", "build"]
            """), Lf(r.Dockerfile));
    }

    [Fact]
    public void SvelteKit_adapter_vercel_gets_adapter_node_from_pnpm()
    {
        using var repo = SvelteKitApp("adapter-vercel", options: "{ runtime: 'nodejs22.x', regions: ['iad1'] }").With("pnpm-lock.yaml", "lockfileVersion: '9.0'");
        var r = Analyze(repo);

        Assert.Equal("SvelteKit (adapter-vercel → adapter-node)", r.Title);
        Assert.Contains("Built with @sveltejs/adapter-node instead of adapter-vercel, without changing the repo", r.Evidence);
        Assert.Contains(Lf($"""
            RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile
            RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store pnpm add -D --ignore-workspace-root-check @sveltejs/adapter-node@5
            {ProjectAnalyzer.SvelteKitNodeSwap}
            RUN pnpm run build
            """), Lf(r.Dockerfile));
    }

    [Fact]
    public void SvelteKit_3_config_in_vite_config_is_switched_too()
    {
        // Kit 3 has no svelte.config.js: the adapter is passed to the sveltekit() plugin.
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/adapter-auto": "^8.0.0", "@sveltejs/kit": "^3.1.0", "vite": "^8.0.12" } }""")
            .With("vite.config.ts", "import adapter from '@sveltejs/adapter-auto';\nimport { sveltekit } from '@sveltejs/kit/vite';\nimport { defineConfig } from 'vite';\n\nexport default defineConfig({ plugins: [sveltekit({ adapter: adapter() })] });\n");
        var r = Analyze(repo);

        Assert.Equal("SvelteKit (adapter-auto → adapter-node)", r.Title);
        Assert.Contains("npm install -D @sveltejs/adapter-node@6\n", r.Dockerfile);
        Assert.Contains("'vite.config.ts'", ProjectAnalyzer.SvelteKitNodeSwap);
    }

    [Fact]
    public void SvelteKit_1_gets_adapter_node_1_from_yarn()
    {
        using var repo = SvelteKitApp("adapter-auto", kit: "^1.27.0").With("yarn.lock", "");
        var r = Analyze(repo);
        Assert.Contains("RUN yarn install --frozen-lockfile\nRUN yarn add -D --ignore-workspace-root-check @sveltejs/adapter-node@1\nRUN node <<'EOF'\n", r.Dockerfile);
    }

    [Fact]
    public void Yarn_berry_adds_adapter_node_without_the_root_check_flag()
    {
        using var repo = SvelteKitApp("adapter-vercel", kit: "^3.0.0").With("yarn.lock", "").With(".yarnrc.yml", "nodeLinker: node-modules\n");
        var r = Analyze(repo);
        Assert.Contains("RUN yarn install --immutable\nRUN yarn add -D @sveltejs/adapter-node@6\n", r.Dockerfile);
    }

    [Theory]
    [InlineData("^1.27.4", "@sveltejs/adapter-node@1")]
    [InlineData("^2.0.0", "@sveltejs/adapter-node@5")]
    [InlineData("~2.70.3", "@sveltejs/adapter-node@5")]
    [InlineData("^3.1.0", "@sveltejs/adapter-node@6")]
    [InlineData("^2.0.0 || ^3.0.0", "@sveltejs/adapter-node@6")]
    [InlineData("^4.0.0", "@sveltejs/adapter-node\n")]
    [InlineData("latest", "@sveltejs/adapter-node\n")]
    public void SvelteKit_gets_the_adapter_node_major_that_matches_kit(string kit, string adapter)
    {
        using var repo = SvelteKitApp("adapter-auto", kit);
        Assert.Contains($"npm install -D {adapter}", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void Workspace_member_adds_adapter_node_from_its_own_folder()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "private": true }""")
            .With("pnpm-workspace.yaml", "packages:\n  - 'apps/*'\n")
            .With("pnpm-lock.yaml", "lockfileVersion: '9.0'")
            .With("apps/web/package.json", """{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/adapter-auto": "^7.0.0", "@sveltejs/kit": "^2.70.0", "vite": "^7.0.0" } }""")
            .With("apps/web/svelte.config.js", "import adapter from '@sveltejs/adapter-auto';\nexport default { kit: { adapter: adapter() } };\n");
        var r = new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(AppDir: "apps/web"));

        Assert.Equal("SvelteKit (adapter-auto → adapter-node)", r.Title);
        Assert.Contains("""
            WORKDIR /app/apps/web
            RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store cd ../.. && pnpm install --frozen-lockfile
            RUN --mount=type=cache,id=pnpm,target=/root/.local/share/pnpm/store pnpm add -D --ignore-workspace-root-check @sveltejs/adapter-node@5
            RUN node <<'EOF'
            """.ReplaceLineEndings("\n"), r.Dockerfile);
    }

    [Fact]
    public void SvelteKit_adapter_node_builds_as_before()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/adapter-node": "^5.2.0", "@sveltejs/kit": "^2.20.0", "svelte": "^5.0.0", "vite": "^6.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("svelte.config.js", "import adapter from '@sveltejs/adapter-node';\nexport default { kit: { adapter: adapter() } };\n");
        var r = Analyze(repo);

        Assert.Equal("SvelteKit (adapter-node)", r.Title);
        Assert.Equal("""
            FROM node:24-slim
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["node", "build"]

            """.ReplaceLineEndings("\n"), r.Dockerfile);
    }

    [Fact]
    public void SvelteKit_with_another_adapter_falls_back_as_before()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/adapter-netlify": "^4.0.0", "@sveltejs/kit": "^2.20.0", "vite": "^6.0.0" } }""")
            .With("svelte.config.js", "import adapter from '@sveltejs/adapter-netlify';\nexport default { kit: { adapter: adapter() } };\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server", r.Title);
        Assert.Contains(r.Warnings, w => w.StartsWith("SvelteKit needs @sveltejs/adapter-node"));
        Assert.Equal("""
            FROM node:24-slim
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["npm", "start"]

            """.ReplaceLineEndings("\n"), r.Dockerfile);
    }

    [Fact]
    public void Astro_vercel_adapter_builds_with_the_node_adapter()
    {
        using var repo = AstroApp("^5.10.0", "@astrojs/vercel", """
            import { defineConfig } from 'astro/config';
            import vercel from '@astrojs/vercel';

            export default defineConfig({ output: 'server', adapter: vercel({ webAnalytics: { enabled: true }, isr: true }) });
            """).With("package-lock.json", "{}");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeServer, r.Kind);
        Assert.Equal("Astro (@astrojs/vercel → @astrojs/node)", r.Title);
        Assert.Equal(4321, r.ContainerPort);
        Assert.Equal(
        [
            "Found package.json; dependencies install with npm from the lockfile",
            "Node 24 (Gitberth's default)",
            "Built with @astrojs/node instead of @astrojs/vercel, without changing the repo",
            "Starts with `node ./dist/server/entry.mjs`, the adapter's standalone server",
            "Port 4321 (Astro's default; the app gets PORT=4321)",
        ], r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Contains(Lf($"""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install -D @astrojs/node@9
            {ProjectAnalyzer.AstroNodeSwap}
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=4321
            # gitberth:volumes
            EXPOSE 4321
            CMD ["node", "./dist/server/entry.mjs"]
            """), Lf(r.Dockerfile));
    }

    [Theory]
    [InlineData("^4.16.0", "@astrojs/vercel/serverless", "@astrojs/node@8")]
    [InlineData("^3.6.0", "@astrojs/vercel/edge", "@astrojs/node@6")]
    [InlineData("^5.10.0", "@astrojs/vercel/serverless", "@astrojs/node@9")]
    public void Astro_vercel_entry_points_all_switch_to_the_node_adapter(string astro, string import, string adapter)
    {
        using var repo = AstroApp(astro, "@astrojs/vercel", $"import vercel from \"{import}\";\nexport default {{ output: \"server\", adapter: vercel() }};\n");
        var r = Analyze(repo);

        Assert.Equal("Astro (@astrojs/vercel → @astrojs/node)", r.Title);
        Assert.Contains($"npm install -D {adapter}\n", r.Dockerfile);
        Assert.Contains("CMD [\"node\", \"./dist/server/entry.mjs\"]", r.Dockerfile);
    }

    [Theory]
    [InlineData("^1.9.0", "@astrojs/node@4")]
    [InlineData("^2.10.0", "@astrojs/node@5")]
    [InlineData("^3.6.0", "@astrojs/node@6")]
    [InlineData("^4.16.0", "@astrojs/node@8")]
    [InlineData("^5.10.0", "@astrojs/node@9")]
    [InlineData("6.3.1", "@astrojs/node@10")]
    [InlineData("^7.2.0", "@astrojs/node@11")]
    [InlineData("^8.0.0", "@astrojs/node\n")]
    [InlineData("catalog:", "@astrojs/node\n")]
    public void Astro_gets_the_node_adapter_major_that_matches_astro(string astro, string adapter)
    {
        using var repo = AstroApp(astro, "@astrojs/vercel", "import vercel from '@astrojs/vercel/serverless';\nexport default { output: 'server', adapter: vercel() };\n");
        Assert.Contains($"npm install -D {adapter}", Analyze(repo).Dockerfile);
    }

    [Theory]
    [InlineData("^4.16.0", "output: 'server', ", ProjectKind.NodeServer)]
    [InlineData("^4.16.0", "output: 'hybrid', ", ProjectKind.NodeServer)]
    [InlineData("^4.16.0", "output: 'static', ", ProjectKind.NodeSpa)]
    [InlineData("^4.16.0", "", ProjectKind.NodeSpa)]
    [InlineData("^5.10.0", "", ProjectKind.NodeServer)]
    public void Astro_5_always_builds_a_server_and_older_ones_only_for_server_output(string astro, string output, ProjectKind kind)
    {
        using var repo = AstroApp(astro, "@astrojs/node", $"import node from '@astrojs/node';\nexport default {{ {output}adapter: node({{ mode: 'standalone' }}) }};\n");
        Assert.Equal(kind, Analyze(repo).Kind);
    }

    [Fact]
    public void Astro_4_static_site_on_the_vercel_adapter_is_served_by_nginx()
    {
        using var repo = AstroApp("^4.16.0", "@astrojs/vercel", "import vercel from '@astrojs/vercel/static';\nexport default { adapter: vercel({ webAnalytics: { enabled: true } }) };\n");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeSpa, r.Kind);
        Assert.Equal("Astro (static, @astrojs/vercel → @astrojs/node)", r.Title);
        Assert.Equal(80, r.ContainerPort);
        Assert.Contains("Built with @astrojs/node instead of @astrojs/vercel, without changing the repo", r.Evidence);
        // The Vercel adapter writes the site to .vercel/output; with the Node adapter a static build lands in dist/.
        Assert.Contains(Lf($"""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install -D @astrojs/node@8
            {ProjectAnalyzer.AstroNodeSwap}
            RUN npm run build
            """), Lf(r.Dockerfile));
        Assert.Contains("COPY --from=build /app/dist/ /usr/share/nginx/html/", r.Dockerfile);
    }

    [Fact]
    public void Astro_node_adapter_runs_its_standalone_server()
    {
        using var repo = AstroApp("^5.10.0", "@astrojs/node", "import node from '@astrojs/node';\nexport default { output: 'server', adapter: node({ mode: 'standalone' }) };\n")
            .With("package-lock.json", "{}");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.NodeServer, r.Kind);
        Assert.Equal("Astro (@astrojs/node)", r.Title);
        Assert.Equal(4321, r.ContainerPort);
        Assert.DoesNotContain(r.Evidence, e => e.StartsWith("Built with", StringComparison.Ordinal));
        Assert.Empty(r.Warnings);
        Assert.Equal("""
            FROM node:24-slim
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=4321
            # gitberth:volumes
            EXPOSE 4321
            CMD ["node", "./dist/server/entry.mjs"]

            """.ReplaceLineEndings("\n"), r.Dockerfile);
    }

    [Fact]
    public void Astro_with_both_adapters_switches_the_import_without_installing()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "astro build" }, "dependencies": { "astro": "^5.10.0", "@astrojs/node": "^9.2.0", "@astrojs/vercel": "^8.2.0" } }""")
            .With("astro.config.mjs", "import node from '@astrojs/node';\nimport vercel from '@astrojs/vercel';\nexport default { adapter: process.env.VERCEL ? vercel() : node({ mode: 'standalone' }) };\n");
        var r = Analyze(repo);

        Assert.Equal("Astro (@astrojs/vercel → @astrojs/node)", r.Title);
        Assert.DoesNotContain("install -D", r.Dockerfile);
        Assert.Contains(ProjectAnalyzer.AstroNodeSwap, r.Dockerfile);
    }

    [Fact]
    public void Astro_node_adapter_in_middleware_mode_runs_the_apps_own_server_as_before()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "astro build", "start": "node server.mjs" }, "dependencies": { "astro": "^5.10.0", "@astrojs/node": "^9.2.0", "express": "^5.0.0" } }""")
            .With("server.mjs", "import express from 'express';\nconst app = express();\napp.listen(process.env.PORT || 8080);\n")
            .With("astro.config.mjs", "import node from '@astrojs/node';\nexport default { output: 'server', adapter: node({ mode: 'middleware' }) };\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (Express)", r.Title);
        Assert.Equal("""
            FROM node:24-slim
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=8080
            # gitberth:volumes
            EXPOSE 8080
            CMD ["npm", "start"]

            """.ReplaceLineEndings("\n"), r.Dockerfile);
    }

    [Fact]
    public void Astro_without_an_adapter_builds_as_before()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "dev": "astro dev", "build": "astro build" }, "dependencies": { "astro": "^5.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("astro.config.mjs", "import { defineConfig } from 'astro/config';\nexport default defineConfig({});\n");
        var r = Analyze(repo);

        Assert.Equal("Astro (static)", r.Title);
        Assert.Equal("""
            FROM node:24-slim AS build
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build

            FROM nginx:stable-alpine
            COPY <<'EOF' /etc/nginx/conf.d/default.conf
            server {
                listen 80;
                root /usr/share/nginx/html;
                index index.html;
                location / {
                    try_files $uri $uri.html $uri/ =404;
                }
            }
            EOF
            COPY --from=build /app/dist/ /usr/share/nginx/html/
            # gitberth:volumes
            EXPOSE 80

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
    }

    [Fact]
    public void Adapter_apps_without_a_start_script_are_offered()
    {
        using var svelte = SvelteKitApp("adapter-auto");
        Assert.Contains(new ProjectAnalyzer().FindApps(svelte.Root), c => c.Title == "SvelteKit (adapter-auto → adapter-node)");
        using var astro = AstroApp("^5.10.0", "@astrojs/node", "import node from '@astrojs/node';\nexport default { adapter: node({ mode: 'standalone' }) };\n");
        Assert.Contains(new ProjectAnalyzer().FindApps(astro.Root), c => c.Title == "Astro (@astrojs/node)");
    }

    [Fact]
    public void Swap_step_keeps_its_script_when_secrets_are_mounted()
    {
        using var repo = SvelteKitApp("adapter-auto");
        var dockerfile = DockerfileMarkers.Expand(Analyze(repo).Dockerfile!, [], [], ["API_TOKEN"]);
        var step = Lf(ProjectAnalyzer.SvelteKitNodeSwap);
        Assert.Contains("RUN --mount=type=secret,id=API_TOKEN,env=API_TOKEN node <<'EOF'\n" + step[(step.IndexOf('\n') + 1)..] + "\n", dockerfile);
    }

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    /// <summary>A SvelteKit app whose svelte.config.js imports the given adapter.</summary>
    private static TempRepo SvelteKitApp(string adapter, string kit = "^2.70.0", string options = "") => new TempRepo()
        .With("package.json", $$"""{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/{{adapter}}": "^7.0.0", "@sveltejs/kit": "{{kit}}", "svelte": "^5.0.0", "vite": "^7.0.0" } }""")
        .With("svelte.config.js", $"import adapter from '@sveltejs/{adapter}';\n\nexport default {{ kit: {{ adapter: adapter({options}) }} }};\n");

    /// <summary>An Astro app with one adapter in package.json and the given astro.config.mjs.</summary>
    private static TempRepo AstroApp(string astro, string adapter, string config) => new TempRepo()
        .With("package.json", $$"""{ "type": "module", "scripts": { "dev": "astro dev", "build": "astro build" }, "dependencies": { "astro": "{{astro}}", "{{adapter}}": "^9.0.0" } }""")
        .With("astro.config.mjs", config);
}
