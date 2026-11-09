using Gitberth.Detection;

namespace Gitberth.Tests;

public class DenoLanguageTests
{
    private const string HonoMain = """
        import { Hono } from "hono";

        const app = new Hono();
        app.get("/", (c) => c.text("hi"));

        Deno.serve(app.fetch);
        """;

    private static DetectionResult Analyze(TempRepo repo, string? hint = null, string? appDir = null) =>
        new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint, AppDir: appDir));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static TempRepo HonoApp() => new TempRepo()
        .With("deno.json", """{ "imports": { "hono": "jsr:@hono/hono@^4" }, "tasks": { "start": "deno run --allow-net main.ts", "dev": "deno run --watch main.ts" } }""")
        .With("main.ts", HonoMain);

    [Fact]
    public void Plain_deno_serve_runs_its_entry_file()
    {
        using var repo = new TempRepo().With("main.ts", "Deno.serve(() => new Response(\"hi\"));\n");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Deno, r.Kind);
        Assert.Equal("Deno (Deno.serve)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Equal(
        [
            "Found main.ts, which serves with Deno's own APIs",
            "Deno 2.9.8 (Gitberth's default)",
            "Starts with `deno run -A main.ts`",
            "Port 8000, Deno.serve's default when it's given no port (it doesn't read PORT)",
        ], r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Equal("""
            FROM denoland/deno:2.9.8
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            # The dependencies download now, into the image, so the app starts without fetching them.
            RUN deno install --entrypoint main.ts
            ENV PORT=8000
            # gitberth:volumes
            EXPOSE 8000
            CMD ["deno", "run", "-A", "main.ts"]

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
    }

    [Fact]
    public void A_start_task_starts_it_with_its_dependencies_installed()
    {
        using var repo = HonoApp();
        var r = Analyze(repo);

        Assert.Equal("Deno (Hono)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("RUN deno install && deno install --entrypoint main.ts\n", r.Dockerfile);
        Assert.Contains("CMD [\"deno\", \"task\", \"start\"]", r.Dockerfile);
        Assert.Contains("Starts with `deno task start` (deno.json: `deno run --allow-net main.ts`)", r.Evidence);
        Assert.Equal(DenoDockerfiles.Ignore, r.DockerIgnore);
    }

    [Fact]
    public void A_default_fetch_export_runs_with_deno_serve_on_PORT()
    {
        using var repo = new TempRepo()
            .With("deno.jsonc", "{\n  // Only a dev task.\n  \"tasks\": { \"dev\": \"deno serve --watch main.ts\" },\n}\n")
            .With("main.ts", "export default {\n  fetch(_req: Request) {\n    return new Response(\"hi\");\n  },\n} satisfies Deno.ServeDefaultExport;\n");
        var r = Analyze(repo);

        Assert.Equal("Deno (deno serve)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec deno serve -A --port ${PORT:-8000} main.ts\"]", r.Dockerfile);
        Assert.Contains("main.ts exports a default fetch handler, so it starts with `deno serve -A --port $PORT main.ts`", r.Evidence);
        Assert.Contains("Listens on PORT, or 8000 when it isn't set (deno serve's --port)", r.Evidence);
        Assert.Contains("RUN deno install --entrypoint main.ts\n", r.Dockerfile);
    }

    [Fact]
    public void A_hono_app_exported_by_default_runs_with_deno_serve()
    {
        using var repo = new TempRepo().With("main.ts", "import { Hono } from \"jsr:@hono/hono\";\n\nconst app = new Hono();\napp.get(\"/\", (c) => c.text(\"hi\"));\n\nexport default app;\n");
        var r = Analyze(repo);

        Assert.Equal("Deno (Hono)", r.Title);
        Assert.Contains("\"exec deno serve -A --port ${PORT:-8000} main.ts\"", r.Dockerfile);
    }

    [Fact]
    public void Fresh_1_builds_and_runs_main_ts_instead_of_its_development_server()
    {
        using var repo = new TempRepo()
            .With("deno.json", """
                {
                  "tasks": {
                    "start": "deno run -A --watch=static/,routes/ dev.ts",
                    "build": "deno run -A dev.ts build",
                    "preview": "deno run -A main.ts"
                  },
                  "imports": { "$fresh/": "https://deno.land/x/fresh@1.7.3/" }
                }
                """)
            .With("main.ts", "import { start } from \"$fresh/server.ts\";\nimport manifest from \"./fresh.gen.ts\";\nawait start(manifest);\n")
            .With("dev.ts", "import dev from \"$fresh/dev.ts\";\nawait dev(import.meta.url, \"./main.ts\");\n")
            .With("routes/index.tsx", "export default function Home() { return <p>hi</p>; }\n");
        var r = Analyze(repo);

        Assert.Equal("Deno (Fresh)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("""
            RUN deno install && deno install --entrypoint main.ts
            RUN deno task build
            ENV PORT=8000
            """.ReplaceLineEndings("\n"), r.Dockerfile);
        Assert.Contains("CMD [\"deno\", \"task\", \"preview\"]", r.Dockerfile);
        Assert.Contains("The start task runs a development server (`deno run -A --watch=static/,routes/ dev.ts`), so it isn't used to start the app", r.Evidence);
        Assert.Contains("Port 8000 (Fresh's default)", r.Evidence);
    }

    [Fact]
    public void Fresh_2_builds_and_serves_its_build_output()
    {
        using var repo = new TempRepo()
            .With("deno.json", """
                {
                  "nodeModulesDir": "auto",
                  "tasks": { "dev": "vite", "build": "vite build", "start": "deno serve -A _fresh/server.js" },
                  "imports": { "fresh": "jsr:@fresh/core@^2.0.0", "preact": "npm:preact@^10.26.6", "vite": "npm:vite@^7.1.3" }
                }
                """)
            .With("main.ts", "import { App, staticFiles } from \"fresh\";\nexport const app = new App();\napp.use(staticFiles());\n")
            .With("vite.config.ts", "import { fresh } from \"@fresh/plugin-vite\";\nexport default { plugins: [fresh()] };\n");
        var r = Analyze(repo);

        Assert.Equal("Deno (Fresh)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("RUN deno install && deno install --entrypoint main.ts\nRUN deno task build\n", r.Dockerfile);
        Assert.Contains("CMD [\"deno\", \"task\", \"start\"]", r.Dockerfile);
        Assert.Contains("Port 8000 (deno serve's default)", r.Evidence);
    }

    [Theory]
    [InlineData("Deno.serve({ port: 3000 }, (_req) => new Response(\"hi\"));", 3000, "Listens on port 3000 (main.ts)")]
    [InlineData("Deno.serve({ port: Number(Deno.env.get(\"PORT\") ?? 4000) }, (_req) => new Response(\"hi\"));", 4000, "Listens on PORT, or 4000 when it isn't set (main.ts)")]
    [InlineData("import { Application } from \"jsr:@oak/oak\";\nconst app = new Application();\nawait app.listen({ port: 8080 });", 8080, "Listens on port 8080 (main.ts)")]
    [InlineData("import { Application } from \"jsr:@oak/oak\";\nconst app = new Application();\nawait app.listen(\":5000\");", 5000, "Listens on port 5000 (main.ts)")]
    [InlineData("const PORT = 7000;\nDeno.serve({ port: PORT }, (_req) => new Response(\"hi\"));", 7000, "Listens on port 7000 (main.ts)")]
    public void Finds_the_port_in_the_code(string code, int port, string evidence)
    {
        using var repo = new TempRepo()
            .With("deno.json", """{ "tasks": { "start": "deno run -A main.ts" } }""")
            .With("main.ts", code + "\n");
        var r = Analyze(repo);

        Assert.Equal(port, r.ContainerPort);
        Assert.Contains(evidence, r.Evidence);
        Assert.Contains($"ENV PORT={port}\n", r.Dockerfile);
    }

    [Fact]
    public void A_serve_task_with_a_port_listens_there()
    {
        using var repo = new TempRepo()
            .With("deno.json", """{ "tasks": { "start": "deno serve -A --port 3000 main.ts" } }""")
            .With("main.ts", "export default { fetch: () => new Response(\"hi\") };\n");
        var r = Analyze(repo);

        Assert.Equal("Deno (deno serve)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("Listens on port 3000 (the task's --port)", r.Evidence);
        Assert.Contains("CMD [\"deno\", \"task\", \"start\"]", r.Dockerfile);
    }

    [Theory]
    [InlineData("""{ "tasks": { "start": "deno run -A main.ts" } }""", "Deno.serve({ hostname: \"127.0.0.1\", port: 8000 }, (_req) => new Response(\"hi\"));", "127.0.0.1")]
    [InlineData("""{ "tasks": { "start": "deno serve -A --host localhost main.ts" } }""", "export default { fetch: () => new Response(\"hi\") };", "localhost")]
    public void Listening_on_localhost_warns(string config, string code, string host)
    {
        using var repo = new TempRepo().With("deno.json", config).With("main.ts", code + "\n");
        Assert.Contains($"The app listens on {host}, which can't be reached from outside the container. Listen on 0.0.0.0 (Deno.serve's default) instead.", Analyze(repo).Warnings);
    }

    [Theory]
    [InlineData("2.1.4", "2.1.4", false)]
    [InlineData("v2.5", "2.5.0", false)]
    [InlineData("2.9", DenoVersions.Default, false)]
    [InlineData("2", DenoVersions.Default, false)]
    [InlineData("v2.x", DenoVersions.Default, false)]
    [InlineData("latest", DenoVersions.Default, false)]
    [InlineData("1.46", "1.46.0", true)]
    [InlineData("1", DenoVersions.LastDeno1, true)]
    [InlineData("3", DenoVersions.Default, true)]
    [InlineData("nightly", DenoVersions.Default, true)]
    public void Picks_a_full_image_version_from_what_the_repo_asks_for(string spec, string version, bool warns)
    {
        var (picked, warning) = DenoVersions.FromSpec(spec);
        Assert.Equal(version, picked);
        Assert.Equal(warns, warning is not null);
    }

    [Fact]
    public void The_version_comes_from_dvmrc_tool_versions_or_mise()
    {
        using var dvmrc = new TempRepo().With("main.ts", "Deno.serve(() => new Response(\"hi\"));\n").With(".dvmrc", "2.1.4\n");
        var r = Analyze(dvmrc);
        Assert.StartsWith("FROM denoland/deno:2.1.4\n", r.Dockerfile);
        Assert.Contains("Deno 2.1.4 (.dvmrc: 2.1.4)", r.Evidence);

        using var mise = new TempRepo().With("main.ts", "Deno.serve(() => new Response(\"hi\"));\n").With("mise.toml", "[tools]\nnode = \"24\"\ndeno = \"2.5.0\"\n");
        Assert.StartsWith("FROM denoland/deno:2.5.0\n", Analyze(mise).Dockerfile);
    }

    [Fact]
    public void Deno_1_caches_the_entry_file_with_deno_cache()
    {
        using var repo = HonoApp().With(".tool-versions", "nodejs 24.0.0\ndeno 1.46.3\n");
        var r = Analyze(repo);

        Assert.StartsWith("FROM denoland/deno:1.46.3\n", r.Dockerfile);
        Assert.Contains("RUN deno cache main.ts\n", r.Dockerfile);
        Assert.DoesNotContain("deno install", r.Dockerfile);
        Assert.Contains("Deno 1 no longer gets updates. Moving to Deno 2 is recommended.", r.Warnings);
    }

    [Fact]
    public void A_deno_json_with_nothing_to_start_isnt_an_app()
    {
        using var repo = new TempRepo()
            .With("deno.json", """{ "name": "@acme/kit", "version": "1.0.0", "exports": "./mod.ts", "fmt": { "lineWidth": 100 } }""")
            .With("mod.ts", "export function hi() { return \"hi\"; }\n")
            .With("examples/server.ts", "Deno.serve(() => new Response(\"hi\"));\n");

        Assert.Equal(ProjectKind.Unknown, Analyze(repo).Kind);
        Assert.Empty(new ProjectAnalyzer().FindApps(repo.Root));
    }

    [Fact]
    public void A_package_json_alone_stays_with_node()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node --experimental-strip-types main.ts" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("main.ts", "import express from \"express\";\nexpress().listen(3000);\n");

        Assert.Equal(ProjectKind.NodeServer, Analyze(repo).Kind);
        Assert.DoesNotContain(new ProjectAnalyzer().FindApps(repo.Root), a => a.Kind == ProjectKind.Deno);
    }

    [Fact]
    public void Next_to_a_node_server_node_wins_and_the_deno_app_is_offered_too()
    {
        using var repo = HonoApp()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "");

        Assert.Equal(ProjectKind.NodeServer, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Deno, Analyze(repo, "deno").Kind);
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "" && a.Hint is null && a.Kind == ProjectKind.NodeServer);
        Assert.Contains(apps, a => a.Dir == "" && a.Hint == "deno" && a.Kind == ProjectKind.Deno);
    }

    [Fact]
    public void Next_to_a_package_json_that_doesnt_serve_deno_wins_and_installs_it()
    {
        using var repo = HonoApp()
            .With("package.json", """{ "scripts": { "css": "tailwindcss -i in.css -o static/app.css" }, "devDependencies": { "tailwindcss": "^4.0.0" } }""");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Deno, r.Kind);
        Assert.Contains("RUN deno install && deno install --entrypoint main.ts\n", r.Dockerfile);
    }

    [Fact]
    public void A_start_task_that_serves_nothing_is_a_worker()
    {
        using var repo = new TempRepo()
            .With("deno.json", """{ "tasks": { "start": "deno run -A --unstable-cron cron.ts" } }""")
            .With("cron.ts", "Deno.cron(\"tick\", \"* * * * *\", () => console.log(\"tick\"));\n");
        var r = Analyze(repo);

        Assert.Equal("Deno worker", r.Title);
        Assert.Null(r.ContainerPort);
        Assert.DoesNotContain("EXPOSE", r.Dockerfile);
        Assert.Contains("RUN deno install --entrypoint cron.ts\n", r.Dockerfile);
    }

    [Fact]
    public void A_deno_app_in_a_sub_folder_is_offered()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("edge/deno.json", """{ "tasks": { "start": "deno run -A main.ts" } }""")
            .With("edge/main.ts", "Deno.serve(() => new Response(\"hi\"));\n");

        Assert.Contains(new ProjectAnalyzer().FindApps(repo.Root), a => a.Dir == "edge" && a.Kind == ProjectKind.Deno);
        Assert.Equal("edge", Analyze(repo, appDir: "edge").BuildContext);
    }

    [Fact]
    public void An_unreadable_deno_json_says_so()
    {
        using var repo = new TempRepo().With("deno.json", """{ "tasks": { "start": "deno run main.ts" }""").With("main.ts", "Deno.serve(() => new Response(\"hi\"));\n");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Unknown, r.Kind);
        Assert.Contains("deno.json isn't valid JSON, so Gitberth can't read its tasks.", r.Warnings);
    }

    [Fact]
    public void Suggests_the_variables_the_code_reads()
    {
        using var repo = HonoApp()
            .With("db.ts", "const url = Deno.env.get(\"DATABASE_URL\");\nconst key = Deno.env.get('API_KEY');\nconst redis = process.env.REDIS_URL;\nconst port = Deno.env.get(\"PORT\");\n")
            .With("db_test.ts", "Deno.env.get(\"ONLY_IN_TESTS\");\n")
            .With("static/app.js", "console.log(process.env.IN_THE_BROWSER);\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.Equal("used in db.ts", env["DATABASE_URL"].Source);
        Assert.True(env["API_KEY"].IsSecret);
        Assert.Contains("REDIS_URL", env.Keys);
        Assert.DoesNotContain("PORT", env.Keys);
        Assert.DoesNotContain("ONLY_IN_TESTS", env.Keys);
        Assert.DoesNotContain("IN_THE_BROWSER", env.Keys);
    }

    [Theory]
    [InlineData("deno run -A main.ts", "main.ts")]
    [InlineData("deno run --allow-net --allow-read=. ./src/app.ts", "src/app.ts")]
    [InlineData("deno serve --port 3000 server.ts", "server.ts")]
    [InlineData("deno serve -A _fresh/server.js", "_fresh/server.js")]
    [InlineData("deno task build && deno run -A main.ts", "main.ts")]
    [InlineData("vite build", null)]
    public void Reads_the_entry_file_a_task_runs(string command, string? entry) => Assert.Equal(entry, DenoSources.TaskEntry(command));

    [Theory]
    [InlineData("deno run -A --watch=static/,routes/ dev.ts", true)]
    [InlineData("deno run -A dev.ts", true)]
    [InlineData("vite", true)]
    [InlineData("deno run -A dev.ts build", false)]
    [InlineData("vite build", false)]
    [InlineData("deno serve -A _fresh/server.js", false)]
    [InlineData("deno run -A main.ts", false)]
    public void Tells_a_development_server_task_apart(string command, bool dev) => Assert.Equal(dev, DenoSources.IsDevServer(command));
}
