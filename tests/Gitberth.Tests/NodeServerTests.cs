using Gitberth.Detection;

namespace Gitberth.Tests;

/// <summary>Node.js and Bun backends: frameworks, start commands, TypeScript, ports, bindings and Bun.</summary>
public class NodeServerTests
{
    private static DetectionResult Analyze(TempRepo repo, string? hint = null) => new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static string Cmd(DetectionResult r) => Lf(r.Dockerfile).Split('\n').Single(l => l.StartsWith("CMD ", StringComparison.Ordinal));

    // ---- Frameworks ----

    [Fact]
    public void Express_generator_starts_bin_www_and_reads_its_port_there()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node ./bin/www" }, "dependencies": { "express": "~4.16.1" } }""")
            .With("app.js", "var express = require('express');\nvar app = express();\nmodule.exports = app;\n")
            .With("bin/www", "#!/usr/bin/env node\nvar app = require('../app');\nvar http = require('http');\nvar port = normalizePort(process.env.PORT || '4000');\nvar server = http.createServer(app);\nserver.listen(port);\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (Express)", r.Title);
        Assert.Equal(4000, r.ContainerPort);
        Assert.Contains("Listens on port 4000 (from bin/www)", r.Evidence);
        Assert.Equal("CMD [\"npm\", \"start\"]", Cmd(r));
    }

    [Fact]
    public void Without_a_start_script_bin_www_runs_rather_than_the_app_it_serves()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "dependencies": { "express": "^5.1.0" } }""")
            .With("app.js", "module.exports = require('express')();\n")
            .With("bin/www", "require('http').createServer(require('../app')).listen(process.env.PORT || 3000);\n");
        Assert.Equal("CMD [\"node\", \"bin/www\"]", Cmd(Analyze(repo)));
    }

    [Theory]
    [InlineData("koa", "Node.js server (Koa)", 3000)]
    [InlineData("@hapi/hapi", "Node.js server (hapi)", 3000)]
    [InlineData("hono", "Node.js server (Hono)", 3000)]
    [InlineData("@strapi/strapi", "Node.js server (Strapi)", 1337)]
    [InlineData("@feathersjs/feathers", "Node.js server (Feathers)", 3030)]
    [InlineData("sails", "Node.js server (Sails)", 1337)]
    [InlineData("@loopback/core", "Node.js server (LoopBack)", 3000)]
    [InlineData("restify", "Node.js server (Restify)", 3000)]
    public void Each_framework_has_its_title_and_default_port(string package, string title, int port)
    {
        using var repo = new TempRepo().With("package.json", $$"""{ "scripts": { "start": "node index.js" }, "dependencies": { "{{package}}": "*" } }""");
        var r = Analyze(repo);
        Assert.Equal(title, r.Title);
        Assert.Equal(port, r.ContainerPort);
        Assert.Contains($"PORT={port}", r.Dockerfile);
    }

    [Fact]
    public void NestJS_builds_and_runs_start_prod()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "nest build", "start": "nest start", "start:dev": "nest start --watch", "start:prod": "node dist/main" }, "dependencies": { "@nestjs/core": "^12.0.0", "@nestjs/platform-express": "^12.0.0" }, "devDependencies": { "@nestjs/cli": "^12.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("src/main.ts", "const app = await NestFactory.create(AppModule);\nawait app.listen(process.env.PORT ?? 3000);\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (NestJS)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("Listens on port 3000 (from src/main.ts)", r.Evidence);
        Assert.Contains(Lf("""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["npm", "run", "start:prod"]
            """), Lf(r.Dockerfile));
    }

    [Theory]
    [InlineData("^5.9.3", """{ "compilerOptions": { "outDir": "./dist" } }""", "", "dist/main.js")]
    [InlineData("^6.0.2", """{ "compilerOptions": { "outDir": "./dist" } }""", "", "dist/src/main.js")]
    [InlineData("^6.0.2", """{ "compilerOptions": { "outDir": "./dist", "rootDir": "./src" } }""", "", "dist/main.js")]
    [InlineData("^6.0.2", """{ "compilerOptions": { "outDir": "./build", "rootDir": "./src" } }""", """{ "sourceRoot": "src", "entryFile": "server" }""", "build/server.js")]
    public void NestJS_without_start_prod_runs_the_build_output_not_nest_start(string typescript, string tsconfig, string nestCli, string built)
    {
        using var repo = new TempRepo()
            .With("package.json", $$"""{ "scripts": { "build": "nest build", "start": "nest start" }, "dependencies": { "@nestjs/core": "^12.0.0" }, "devDependencies": { "typescript": "{{typescript}}" } }""")
            .With("tsconfig.json", tsconfig)
            .With("src/main.ts", "await app.listen(process.env.PORT ?? 4000);\n")
            .With("src/server.ts", "await app.listen(process.env.PORT ?? 4000);\n");
        if (nestCli.Length > 0) repo.With("nest-cli.json", nestCli);
        var r = Analyze(repo);

        Assert.Equal($"CMD [\"node\", \"{built}\"]", Cmd(r));
        Assert.Contains("RUN npm run build\n", Lf(r.Dockerfile));
        Assert.Contains(r.Evidence, e => e.Contains("rather than `nest start`", StringComparison.Ordinal));
        Assert.Equal(4000, r.ContainerPort);
    }

    [Fact]
    public void AdonisJS_builds_with_ace_and_starts_from_build()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "start": "node bin/server.js", "build": "node ace build", "dev": "node ace serve --hmr" }, "dependencies": { "@adonisjs/core": "^7.0.0" }, "devDependencies": { "@adonisjs/assembler": "^8.0.0" } }""")
            .With("package-lock.json", "{}")
            .With("adonisrc.ts", "export default defineConfig({})\n")
            .With("ace.js", "await import('./bin/console.js')\n")
            .With("bin/server.ts", "new Ignitor(APP_ROOT).httpServer().start()\n")
            .With("start/env.ts", "export default await Env.create(new URL('../', import.meta.url), {\n  NODE_ENV: Env.schema.enum(['development', 'production', 'test'] as const),\n  PORT: Env.schema.number(),\n  APP_KEY: Env.schema.string(),\n  HOST: Env.schema.string({ format: 'host' }),\n  LOG_LEVEL: Env.schema.string(),\n})\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (AdonisJS)", r.Title);
        Assert.Equal(3333, r.ContainerPort);
        Assert.Contains("Port 3333 (AdonisJS's default; the app gets PORT=3333)", r.Evidence);
        Assert.Contains(Lf("""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm ci
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3333 LOG_LEVEL=info
            # gitberth:volumes
            EXPOSE 3333
            WORKDIR /app/build
            CMD ["node", "bin/server.js"]
            """), Lf(r.Dockerfile));
        Assert.Contains(r.Warnings, w => w.StartsWith("AdonisJS won't start without APP_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void Strapi_reads_its_port_from_config_and_says_what_it_needs()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "strapi build", "develop": "strapi develop", "start": "strapi start" }, "dependencies": { "@strapi/strapi": "^5.0.0", "better-sqlite3": "^12.11.1" } }""")
            .With("config/server.js", "module.exports = ({ env }) => ({\n  host: env('HOST', '0.0.0.0'),\n  port: env.int('PORT', 1340),\n  app: { keys: env.array('APP_KEYS') },\n});\n")
            .With("config/admin.js", "module.exports = ({ env }) => ({ auth: { secret: env('ADMIN_JWT_SECRET') }, apiToken: { salt: env('API_TOKEN_SALT') }, flags: { nps: env.bool('FLAG_NPS', true) } });\n")
            .With("config/database.js", "module.exports = ({ env }) => {\n  const client = env('DATABASE_CLIENT', 'sqlite');\n  return { connection: { client, connection: { connectionString: env('DATABASE_URL') } } };\n};\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (Strapi)", r.Title);
        Assert.Equal(1340, r.ContainerPort);
        Assert.Contains("Listens on port 1340 (from config/server.js)", r.Evidence);
        Assert.Equal("CMD [\"npm\", \"start\"]", Cmd(r));
        Assert.Contains("Strapi won't start without APP_KEYS, ADMIN_JWT_SECRET and API_TOKEN_SALT, which its config reads with no default. Set them on the Environment tab.", r.Warnings);
        Assert.Contains(r.Warnings, w => w.StartsWith("Strapi keeps its SQLite database in .tmp/data.db", StringComparison.Ordinal) && w.Contains("/app/.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Feathers_compiles_with_its_compile_script_and_listens_where_its_config_says()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "dev": "nodemon -x ts-node src/index.ts", "compile": "shx rm -rf lib/ && tsc", "start": "node lib/" }, "dependencies": { "@feathersjs/feathers": "^5.0.0", "@feathersjs/koa": "^5.0.0" }, "devDependencies": { "typescript": "^5.9.0" } }""")
            .With("config/default.json", """{ "host": "localhost", "port": 3030, "public": "./public/" }""")
            .With("src/index.ts", "const port = app.get('port')\napp.listen(port)\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (Feathers)", r.Title);
        Assert.Equal(3030, r.ContainerPort);
        Assert.Contains("RUN npm run compile\n", Lf(r.Dockerfile));
        Assert.Equal("CMD [\"npm\", \"start\"]", Cmd(r));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("isn't in the repo", StringComparison.Ordinal));
    }

    // ---- Where it listens ----

    [Theory]
    [InlineData("app.listen(process.env.PORT || 4000)", 4000)]
    [InlineData("const port = Number(process.env.PORT) || 4001;", 4001)]
    [InlineData("const port = parseInt(process.env.PORT, 10) || 4002;", 4002)]
    [InlineData("const port = process.env.PORT ?? '4003';", 4003)]
    [InlineData("const { PORT = 4004 } = process.env;", 4004)]
    [InlineData("export const env = z.object({ PORT: z.coerce.number().default(4005) });", 4005)]
    [InlineData("cleanEnv(process.env, { PORT: port({ default: 4006 }) });", 4006)]
    [InlineData("app.listen(4007, () => console.log('up'));", 4007)]
    [InlineData("await fastify.listen({ port: 4008, host: '0.0.0.0' });", 4008)]
    [InlineData("serve({ fetch: app.fetch, port: 4009 });", 4009)]
    [InlineData("const PORT = 4010;\napp.listen(PORT);", 4010)]
    [InlineData("const port = 4011;\nserve({ fetch: app.fetch, port });", 4011)]
    [InlineData("Bun.serve({ port: 4012, fetch() { return new Response('hi'); } });", 4012)]
    [InlineData("export default {\n  port: 4013,\n  fetch: app.fetch,\n};", 4013)]
    [InlineData("const server = Hapi.server({ port: 4014, host: '0.0.0.0' });", 4014)]
    [InlineData("const port = process.env.PORT ? Number(process.env.PORT) : 4015;", 4015)]
    [InlineData("app.listen(Bun.env.PORT ?? 4016);", 4016)]
    [InlineData("port: env.int('PORT', 4017),", 4017)]
    [InlineData("const port: number = 4018;\nserver.listen(port);", 4018)]
    public void Port_is_read_from_the_code(string code, int port) =>
        Assert.Equal(port, ProjectAnalyzer.PortInCode(code));

    [Theory]
    [InlineData("const pool = new Pool({ host: 'db', port: 5432 });\napp.listen(process.env.PORT);")]
    [InlineData("const options = { port: process.env.PORT, timeout: 5000 };")]
    [InlineData("const DB_PORT = process.env.DB_PORT || 5432;")]
    public void Other_ports_in_the_code_are_left_alone(string code) =>
        Assert.Null(ProjectAnalyzer.PortInCode(code));

    [Fact]
    public void A_port_in_the_start_script_wins()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "PORT=4500 node server.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("server.js", "app.listen(process.env.PORT || 3000);\n");
        var r = Analyze(repo);
        Assert.Equal(4500, r.ContainerPort);
        Assert.Contains("Listens on port 4500 (from the start script)", r.Evidence);
    }

    [Fact]
    public void The_port_can_come_from_a_file_the_entry_imports()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "start": "node src/main.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("src/main.js", "import { config } from './settings.js';\nimport { app } from './app.js';\napp.listen(config.port);\n")
            .With("src/settings.js", "export const config = { port: Number(process.env.PORT) || 4600 };\n")
            .With("src/app.js", "export const app = express();\n");
        Assert.Equal(4600, Analyze(repo).ContainerPort);
    }

    [Theory]
    [InlineData("app.listen(3000, 'localhost');")]
    [InlineData("app.listen(Number(process.env.PORT) || 3000, '127.0.0.1', () => {});")]
    [InlineData("const server = Hapi.server({ port: 3000, host: 'localhost' });")]
    [InlineData("const HOST = '127.0.0.1';\napp.listen(PORT, HOST);")]
    [InlineData("serve({ fetch: app.fetch, port: 3000, hostname: 'localhost' });")]
    public void A_server_pinned_to_localhost_is_warned(string code)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("server.js", code);
        Assert.Contains("server.js listens on localhost only, so nothing outside the container can reach it. Listen on 0.0.0.0, or on process.env.HOST, which Gitberth sets to 0.0.0.0.", Analyze(repo).Warnings);
    }

    [Theory]
    [InlineData("app.listen(process.env.PORT || 3000, process.env.HOST || 'localhost');")]
    [InlineData("const pool = new Pool({ host: 'localhost', port: 5432 });\napp.listen(3000);")]
    [InlineData("// app.listen(3000, 'localhost') was the old way\napp.listen(3000);")]
    public void A_server_that_can_listen_everywhere_is_not_warned(string code)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("server.js", code);
        Assert.DoesNotContain(Analyze(repo).Warnings, w => w.Contains("localhost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fastify.listen({ port: 3000 })", true)]
    [InlineData("fastify.listen(3000)", true)]
    [InlineData("fastify.listen({ port: Number(process.env.PORT) || 3000, host: '0.0.0.0' })", false)]
    [InlineData("fastify.listen({ port, host: process.env.HOST })", false)]
    [InlineData("fastify.listen(3000, '0.0.0.0')", false)]
    [InlineData("fastify.listen({ port: 3000, host: '::' })", false)]
    public void Fastify_without_a_host_is_warned(string listen, bool warned)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "fastify": "^5.0.0" } }""")
            .With("server.js", $"const fastify = require('fastify')();\nfastify.get('/', async () => 'hi');\n{listen};\n");
        var warnings = Analyze(repo).Warnings;
        Assert.Equal(warned, warnings.Any(w => w.StartsWith("server.js: Fastify listens on localhost unless listen() gets a host", StringComparison.Ordinal)));
    }

    [Fact]
    public void Fastify_cli_is_told_to_listen_on_every_interface()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "fastify start -l info app.js", "dev": "fastify start -w -l info -P app.js" }, "dependencies": { "fastify": "^5.0.0", "fastify-cli": "^7.0.0" } }""")
            .With("app.js", "module.exports = async function (fastify, opts) {};\n");
        var r = Analyze(repo);
        Assert.Contains("ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000 FASTIFY_ADDRESS=0.0.0.0\n", Lf(r.Dockerfile));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Fastify listens on localhost", StringComparison.Ordinal));
    }

    [Fact]
    public void NestJS_on_Fastify_without_a_host_is_warned()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "nest build", "start:prod": "node dist/main" }, "dependencies": { "@nestjs/core": "^12.0.0", "@nestjs/platform-fastify": "^12.0.0" } }""")
            .With("src/main.ts", "const app = await NestFactory.create<NestFastifyApplication>(AppModule, new FastifyAdapter());\nawait app.listen(process.env.PORT ?? 3000);\n");
        Assert.Contains(Analyze(repo).Warnings, w => w.StartsWith("src/main.ts: NestJS on Fastify listens on localhost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("express-session", "", true)]
    [InlineData("passport", "app.set('trust proxy', 1);", false)]
    [InlineData("cors", "", false)]
    public void Sessions_behind_the_tunnel_need_express_to_trust_the_proxy(string package, string setting, bool warned)
    {
        using var repo = new TempRepo()
            .With("package.json", $$"""{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.1.0", "{{package}}": "*" } }""")
            .With("server.js", $"const app = require('express')();\n{setting}\napp.listen(process.env.PORT || 3000);\n");
        Assert.Equal(warned, Analyze(repo).Warnings.Any(w => w.Contains("app.set('trust proxy', 1)", StringComparison.Ordinal)));
    }

    // ---- How it starts ----

    [Theory]
    [InlineData("nodemon server.js", "server.js", "[\"node\", \"server.js\"]")]
    [InlineData("node --watch server.js", "server.js", "[\"node\", \"server.js\"]")]
    [InlineData("nodemon --watch src --exec node src/app.js", "src/app.js", "[\"node\", \"src/app.js\"]")]
    [InlineData("npm run dev", "app.js", "[\"node\", \"app.js\"]")]
    public void A_start_script_that_runs_a_dev_server_runs_its_file_directly(string start, string file, string cmd)
    {
        using var repo = new TempRepo()
            .With("package.json", $$"""{ "scripts": { "start": "{{start}}", "dev": "nodemon app.js" }, "dependencies": { "express": "^5.1.0" }, "devDependencies": { "nodemon": "^3.0.0" } }""")
            .With(file, "require('express')().listen(process.env.PORT || 3000);\n");
        var r = Analyze(repo);
        Assert.Equal($"CMD {cmd}", Cmd(r));
        Assert.Contains(r.Evidence, e => e.StartsWith("The start script (`", StringComparison.Ordinal) && e.EndsWith("is for development, so Gitberth runs its file directly", StringComparison.Ordinal));
    }

    [Fact]
    public void Start_prod_wins_over_a_development_start_script()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "tsc", "start": "ts-node-dev --respawn src/server.ts", "start:prod": "node dist/server.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("src/server.ts", "app.listen(process.env.PORT || 3000);\n");
        Assert.Equal("CMD [\"npm\", \"run\", \"start:prod\"]", Cmd(Analyze(repo)));
    }

    [Fact]
    public void A_next_app_whose_start_script_runs_next_dev_runs_next_start()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "next build", "start": "next dev" }, "dependencies": { "next": "^16.0.0" } }""");
        var r = Analyze(repo);
        Assert.Equal("CMD [\"npx\", \"next\", \"start\"]", Cmd(r));
        Assert.Contains("The start script (`next dev`) runs a development server, so Gitberth runs `next start` instead", r.Evidence);
    }

    [Fact]
    public void A_missing_entry_gets_a_precise_warning()
    {
        using var repo = new TempRepo().With("package.json", """{ "main": "index.js", "dependencies": { "express": "^5.1.0" } }""");
        var r = Analyze(repo);
        Assert.Contains("package.json's \"main\" is index.js, which isn't in the repo, and no build script makes it. No start script or server file was found (server.js, index.js, app.js, bin/www, src/index.ts and the like). Add a \"start\" script to package.json, or customize the Dockerfile.", r.Warnings);
        Assert.Equal("CMD [\"npm\", \"start\"]", Cmd(r));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("tsc", false)]
    public void A_start_script_that_runs_a_missing_file_is_warned_unless_a_build_makes_it(string? build, bool warned)
    {
        var scripts = build is null ? """{ "start": "node dist/server.js" }""" : $$"""{ "build": "{{build}}", "start": "node dist/server.js" }""";
        using var repo = new TempRepo().With("package.json", $$"""{ "scripts": {{scripts}}, "dependencies": { "express": "^5.1.0" } }""");
        var warnings = Analyze(repo).Warnings;
        Assert.Equal(warned, warnings.Contains("The \"start\" script runs dist/server.js, which isn't in the repo, and there's no build script to make it."));
    }

    [Fact]
    public void A_start_script_that_runs_typescript_on_an_old_node_is_warned()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "start": "node src/index.ts" }, "dependencies": { "express": "^5.1.0" } }""")
            .With(".nvmrc", "20")
            .With("src/index.ts", "app.listen(3000);\n");
        Assert.Contains("The \"start\" script runs src/index.ts with node, which can't run it, because Node 20 doesn't strip types (22.18 and later do). Run it with tsx (`node --import tsx src/index.ts`), or compile it in a \"build\" script.", Analyze(repo).Warnings);
    }

    [Theory]
    [InlineData("node --import tsx src/index.ts")]
    [InlineData("tsx src/index.ts")]
    [InlineData("node src/index.ts")]
    public void A_start_script_that_runs_typescript_node_can_run_is_not_warned(string start)
    {
        using var repo = new TempRepo()
            .With("package.json", $$"""{ "type": "module", "scripts": { "start": "{{start}}" }, "dependencies": { "express": "^5.1.0" }, "devDependencies": { "tsx": "^4.0.0" } }""")
            .With("src/index.ts", "const port: number = 3000;\napp.listen(port);\n");
        Assert.DoesNotContain(Analyze(repo).Warnings, w => w.Contains("can't run it", StringComparison.Ordinal));
    }

    // ---- TypeScript ----

    [Fact]
    public void TypeScript_built_by_tsc_runs_the_main_the_build_writes()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "main": "dist/server.js", "scripts": { "build": "tsc" }, "devDependencies": { "typescript": "^6.0.2" } }""")
            .With("tsconfig.json", """{ "compilerOptions": { "rootDir": "./src", "outDir": "./dist" } }""")
            .With("src/server.ts", "createServer(handler).listen(Number(process.env.PORT) || 4100);\n");
        var r = Analyze(repo);
        Assert.Equal("Node.js server", r.Title);
        Assert.Equal(4100, r.ContainerPort);
        Assert.Contains("Listens on port 4100 (from src/server.ts)", r.Evidence);
        Assert.Contains(Lf("""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install
            RUN npm run build
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=4100
            # gitberth:volumes
            EXPOSE 4100
            CMD ["node", "dist/server.js"]
            """), Lf(r.Dockerfile));
    }

    [Theory]
    [InlineData("tsc", "^5.9.3", """{ "compilerOptions": { "outDir": "dist" } }""", "dist/index.js")]
    [InlineData("tsc", "^6.0.2", """{ "compilerOptions": { "outDir": "dist" } }""", "dist/src/index.js")]
    [InlineData("tsc -p tsconfig.build.json", "^6.0.2", """{ "compilerOptions": { "outDir": "out" } }""", "out/src/index.js")]
    [InlineData("tsc", "^5.9.3", """{ "compilerOptions": { "outDir": "build", "rootDir": "." } }""", "build/src/index.js")]
    [InlineData("tsc", "^5.9.3", """{ "compilerOptions": {} }""", "src/index.js")]
    [InlineData("tsup src/index.ts --format esm", "^5.9.3", """{ "compilerOptions": {} }""", "dist/index.js")]
    public void TypeScript_with_a_build_and_no_start_or_main_runs_what_the_build_writes(string build, string typescript, string tsconfig, string built)
    {
        using var repo = new TempRepo()
            .With("package.json", $$"""{ "scripts": { "build": "{{build}}" }, "devDependencies": { "typescript": "{{typescript}}" } }""")
            .With(build.Contains("tsconfig.build.json", StringComparison.Ordinal) ? "tsconfig.build.json" : "tsconfig.json", tsconfig)
            .With("src/index.ts", "createServer(handler).listen(3000);\n");
        Assert.Equal($"CMD [\"node\", \"{built}\"]", Cmd(Analyze(repo)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "compilerOptions": { "noEmit": true, "verbatimModuleSyntax": true, "erasableSyntaxOnly": true } }""")]
    public void TypeScript_with_no_build_step_runs_on_node_when_node_can_strip_its_types(string? tsconfig)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module" }""")
            .With("src/index.ts", "import { createServer, type Server } from 'node:http';\nimport { greet } from './greet.ts';\nconst server: Server = createServer((_req, res) => res.end(greet()));\nserver.listen(Number(process.env.PORT) || 3000);\n")
            .With("src/greet.ts", "import type { Options } from './types.js';\nexport function greet(options?: Options): string {\n  return 'hi';\n}\n")
            .With("src/types.ts", "export interface Options { loud: boolean }\n");
        if (tsconfig is not null) repo.With("tsconfig.json", tsconfig);
        var r = Analyze(repo);
        Assert.Equal("CMD [\"node\", \"src/index.ts\"]", Cmd(r));
        Assert.Contains("Node 24 runs src/index.ts by stripping its types, so there's no build step", r.Evidence);
        Assert.DoesNotContain("tsx", r.Dockerfile);
    }

    [Theory]
    [InlineData("src/index.ts", "export enum Color { Red, Green }\n", "src/index.ts declares an enum")]
    [InlineData("src/index.ts", "namespace Shapes {\n  export const pi = 3.14;\n}\n", "src/index.ts declares a namespace")]
    [InlineData("src/index.ts", "class Store {\n  constructor(private readonly db: Db) {}\n}\n", "src/index.ts uses constructor parameter properties")]
    [InlineData("src/index.ts", "@Controller()\nexport class Home {}\n", "src/index.ts uses decorators")]
    [InlineData("src/index.ts", "import fs = require('fs');\n", "src/index.ts uses `import … =`")]
    [InlineData("src/index.ts", "import { db } from './db.js';\n", "src/index.ts imports ./db.js, which is db.ts in the repo")]
    [InlineData("src/index.ts", "import { db } from './db';\n", "src/index.ts imports ./db without its file extension")]
    [InlineData("src/index.ts", "export type Options = { loud: boolean }\nimport { db } from './db.js'\n", "src/index.ts imports ./db.js, which is db.ts in the repo")]
    [InlineData("src/index.ts", "import { db } from './db.ts';\nimport { kinds } from './kinds.ts';\n", "src/kinds.ts declares an enum")]
    public void TypeScript_node_cannot_strip_runs_with_tsx_which_gitberth_adds(string file, string code, string why)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module" }""")
            .With("src/db.ts", "export const db = {};\n")
            .With("src/kinds.ts", "export const enum Kind { A }\n")
            .With(file, code + "createServer(handler).listen(3000);\n");
        var r = Analyze(repo);
        Assert.Contains(Lf("""
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install
            RUN --mount=type=cache,id=npm,target=/root/.npm npm install -D tsx@4
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["node", "--import", "tsx", "src/index.ts"]
            """), Lf(r.Dockerfile));
        Assert.Contains($"Gitberth adds tsx to the image to run src/index.ts, because {why}", r.Evidence);
    }

    [Theory]
    [InlineData("""{ "compilerOptions": { "strict": true } }""", "tsconfig.json doesn't set verbatimModuleSyntax, so type-only imports may not say `import type`")]
    [InlineData("""{ "compilerOptions": { "verbatimModuleSyntax": true, "paths": { "@/*": ["./src/*"] } } }""", "src/index.ts imports @/db through tsconfig paths")]
    public void A_tsconfig_that_node_ignores_means_tsx(string tsconfig, string why)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module" }""")
            .With("tsconfig.json", tsconfig)
            .With("src/index.ts", "import { db } from '@/db';\ncreateServer(handler).listen(3000);\n");
        Assert.Contains($"Gitberth adds tsx to the image to run src/index.ts, because {why}", Analyze(repo).Evidence);
    }

    [Fact]
    public void TypeScript_on_an_old_node_runs_with_tsx()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "engines": { "node": "20.x" } }""")
            .With("src/index.ts", "createServer(handler).listen(3000);\n");
        var r = Analyze(repo);
        Assert.Contains("FROM node:20-slim", r.Dockerfile);
        Assert.Contains("Gitberth adds tsx to the image to run src/index.ts, because Node 20 doesn't strip types (22.18 and later do)", r.Evidence);
    }

    [Fact]
    public void TypeScript_runs_with_the_apps_own_tsx_when_it_has_one()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "dev": "tsx watch src/index.ts" }, "devDependencies": { "tsx": "^4.19.0" } }""")
            .With("package-lock.json", "{}")
            .With("src/index.ts", "enum Mode { Dev, Prod }\ncreateServer(handler).listen(3000);\n");
        var r = Analyze(repo);
        Assert.Equal("CMD [\"node\", \"--import\", \"tsx\", \"src/index.ts\"]", Cmd(r));
        Assert.DoesNotContain("install -D tsx", r.Dockerfile);
        Assert.Contains("tsx runs src/index.ts, because src/index.ts declares an enum", r.Evidence);
        Assert.Contains("The dev script (`tsx watch src/index.ts`) is for development, so Gitberth runs its file directly", r.Evidence);
    }

    [Fact]
    public void Decorators_that_need_their_metadata_are_warned()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module" }""")
            .With("tsconfig.json", """{ "compilerOptions": { "experimentalDecorators": true, "emitDecoratorMetadata": true, "verbatimModuleSyntax": true } }""")
            .With("src/index.ts", "@Service()\nclass Mailer {}\ncreateServer(handler).listen(3000);\n");
        Assert.Contains(Analyze(repo).Warnings, w => w.StartsWith("src/index.ts uses decorators with emitDecoratorMetadata, which tsx can't emit", StringComparison.Ordinal));
    }

    [Fact]
    public void Type_only_imports_are_removed_whole_so_their_paths_do_not_matter()
    {
        using var repo = new TempRepo()
            .With("src/index.ts", "import type { Db } from './db.js';\nexport type { Kind } from './kinds';\nconst db: Db | null = null;\n")
            .With("src/db.ts", "export interface Db {}\n");
        Assert.Null(TypeStripping.Check(repo.Root, "src/index.ts", 24).Why);
    }

    // ---- Bun ----

    [Fact]
    public void Elysia_runs_on_bun()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "dev": "bun run --watch src/index.ts" }, "dependencies": { "elysia": "^1.4.0" } }""")
            .With("bun.lock", "{}")
            .With("src/index.ts", "new Elysia().get('/', () => 'hi').listen(3000);\n");
        var r = Analyze(repo);

        Assert.Equal("Bun server (Elysia)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("Runs on Bun (oven/bun:1), because its dev script runs bun", r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Equal(Lf("""
            FROM oven/bun:1
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache bun install --frozen-lockfile
            ENV NODE_ENV=production HOST=0.0.0.0 PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["bun", "run", "src/index.ts"]

            """), Lf(r.Dockerfile));
    }

    [Theory]
    [InlineData("""{ "scripts": { "start": "bun run src/index.ts" } }""", "its start script runs bun")]
    [InlineData("""{ "scripts": { "start": "NODE_ENV=production bun dist/server.js", "build": "bun build src/server.ts --outdir dist" } }""", "its start script runs bun")]
    [InlineData("""{ "dependencies": { "elysia": "^1.4.0" } }""", "Elysia runs on Bun")]
    [InlineData("""{ "dependencies": { "hono": "^4.6.0" }, "devDependencies": { "@types/bun": "^1.2.0" } }""", "it's written for Bun (it has Bun's types)")]
    [InlineData("""{ "packageManager": "bun@1.2.21" }""", "it installs with bun and has no start script")]
    public void What_makes_an_app_run_on_bun(string json, string reason)
    {
        using var repo = new TempRepo().With("package.json", json).With("src/index.ts", "export default { fetch: () => new Response('hi') };\n");
        Assert.Contains($"Runs on Bun (oven/bun:{(json.Contains("1.2.21", StringComparison.Ordinal) ? "1.2.21" : "1")}), because {reason}", Analyze(repo).Evidence);
    }

    [Fact]
    public void Code_that_uses_bun_apis_runs_on_bun()
    {
        using var repo = new TempRepo()
            .With("package.json", "{}")
            .With("index.ts", "Bun.serve({ port: 4200, fetch: () => new Response(Bun.env.GREETING) });\n");
        var r = Analyze(repo);
        Assert.Equal("Bun server", r.Title);
        Assert.Equal(4200, r.ContainerPort);
        Assert.Contains("Runs on Bun (oven/bun:1), because index.ts uses Bun's APIs", r.Evidence);
        Assert.Contains(r.EnvSuggestions, s => s.Key == "GREETING" && s.Source == "used in index.ts");
    }

    [Theory]
    [InlineData("""{ "packageManager": "bun@1.2.21+sha512.abc", "scripts": { "start": "bun src/index.ts" } }""", "oven/bun:1.2.21")]
    [InlineData("""{ "engines": { "bun": ">=1.1.0" }, "scripts": { "start": "bun src/index.ts" } }""", "oven/bun:1")]
    [InlineData("""{ "engines": { "bun": "1.3.2" }, "scripts": { "start": "bun src/index.ts" } }""", "oven/bun:1.3.2")]
    public void The_bun_image_follows_the_version_the_app_asks_for(string json, string image)
    {
        using var repo = new TempRepo().With("package.json", json).With("src/index.ts", "Bun.serve({ fetch: () => new Response('hi') });\n");
        var r = Analyze(repo);
        Assert.StartsWith($"FROM {image}\n", Lf(r.Dockerfile));
        Assert.Equal("CMD [\"bun\", \"run\", \"start\"]", Cmd(r));
    }

    [Fact]
    public void A_node_server_with_a_bun_lockfile_installs_with_bun_and_runs_on_node()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.1.0" } }""")
            .With("bun.lockb", "")
            .With("server.js", "require('express')().listen(process.env.PORT || 3000);\n");
        var r = Analyze(repo);

        Assert.Equal("Node.js server (Express)", r.Title);
        Assert.Contains("Found package.json; dependencies install with bun from the lockfile", r.Evidence);
        Assert.Contains("Node 24 (Gitberth's default)", r.Evidence);
        Assert.Contains(Lf("""
            FROM node:24-slim
            WORKDIR /app
            # bun installs the dependencies from its lockfile; Node runs the app.
            COPY --from=oven/bun:1 /usr/local/bin/bun /usr/local/bin/bun
            RUN ln -s bun /usr/local/bin/bunx
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache bun install --frozen-lockfile
            """), Lf(r.Dockerfile));
        Assert.Equal("CMD [\"bun\", \"run\", \"start\"]", Cmd(r));
    }

    [Fact]
    public void A_front_end_with_a_bun_lockfile_builds_with_bun_on_node()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "next build", "start": "next start" }, "dependencies": { "next": "^16.0.0" }, "devDependencies": { "@types/bun": "^1.2.0" } }""")
            .With("bun.lock", "{}")
            .With("next.config.mjs", "export default { output: 'standalone' };");
        var r = Analyze(repo);

        Assert.Equal("Next.js (standalone)", r.Title);
        Assert.Contains("COPY --from=oven/bun:1 /usr/local/bin/bun /usr/local/bin/bun", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache bun install --frozen-lockfile\nRUN bun run build\n", Lf(r.Dockerfile));
        Assert.Contains("CMD [\"node\", \"server.js\"]", r.Dockerfile);
        Assert.DoesNotContain("FROM oven/bun", r.Dockerfile);
    }

    [Fact]
    public void The_sveltekit_adapter_swap_adds_adapter_node_with_bun()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "type": "module", "scripts": { "build": "vite build" }, "devDependencies": { "@sveltejs/adapter-auto": "^7.0.0", "@sveltejs/kit": "^2.70.0", "vite": "^7.0.0" } }""")
            .With("bun.lock", "{}")
            .With("svelte.config.js", "import adapter from '@sveltejs/adapter-auto';\nexport default { kit: { adapter: adapter() } };\n");
        var r = Analyze(repo);
        Assert.Contains("RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache bun add -d @sveltejs/adapter-node@5\nRUN node <<'EOF'\n", Lf(r.Dockerfile));
        Assert.Contains("COPY --from=oven/bun:1 /usr/local/bin/bun /usr/local/bin/bun", r.Dockerfile);
    }

    [Fact]
    public void A_vercel_install_command_that_runs_bun_gets_bun()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("vercel.json", """{ "installCommand": "bun install", "buildCommand": "bun run build" }""");
        var r = Analyze(repo);
        Assert.Contains("COPY --from=oven/bun:1 /usr/local/bin/bun /usr/local/bin/bun", r.Dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=bun,target=/root/.bun/install/cache bun install\nRUN bun run build\n", Lf(r.Dockerfile));
    }

    [Fact]
    public void Bun_env_references_are_suggested()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "start": "bun src/index.ts" } }""")
            .With("src/index.ts", "const token = Bun.env.API_TOKEN;\nconst url = Bun.env[\"DATABASE_URL\"];\nconst port = Bun.env.PORT;\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);
        Assert.True(env["API_TOKEN"].IsSecret);
        Assert.Equal("used in src/index.ts", env["DATABASE_URL"].Source);
        Assert.DoesNotContain("PORT", env.Keys);
    }

    // ---- Which folders are apps ----

    [Fact]
    public void A_typescript_server_without_a_start_script_is_offered_but_a_typescript_library_is_not()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "private": true }""")
            .With("apps/api/package.json", """{ "type": "module", "main": "dist/server.js", "scripts": { "build": "tsc" }, "devDependencies": { "typescript": "^6.0.2" } }""")
            .With("apps/api/tsconfig.json", """{ "compilerOptions": { "rootDir": "src", "outDir": "dist" } }""")
            .With("apps/api/src/server.ts", "import { createServer } from 'node:http';\ncreateServer((_q, s) => s.end('hi')).listen(3000);\n")
            .With("packages/util/package.json", """{ "name": "util", "main": "dist/index.js", "scripts": { "build": "tsc" }, "devDependencies": { "typescript": "^6.0.2" } }""")
            .With("packages/util/tsconfig.json", """{ "compilerOptions": { "rootDir": "src", "outDir": "dist" } }""")
            .With("packages/util/src/index.ts", "export const add = (a: number, b: number) => a + b;\n");
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "apps/api" && a.Kind == ProjectKind.NodeServer);
        Assert.DoesNotContain(apps, a => a.Dir == "packages/util");
    }

    // ---- Reading scripts ----

    [Theory]
    [InlineData("nodemon server.js", true)]
    [InlineData("ts-node-dev --respawn src/index.ts", true)]
    [InlineData("tsx watch src/index.ts", true)]
    [InlineData("node --watch server.js", true)]
    [InlineData("bun --hot src/index.ts", true)]
    [InlineData("bun run --watch src/index.ts", true)]
    [InlineData("next dev", true)]
    [InlineData("vite", true)]
    [InlineData("nest start --watch", true)]
    [InlineData("node ace serve --hmr", true)]
    [InlineData("strapi develop", true)]
    [InlineData("npm run dev", true)]
    [InlineData("node server.js", false)]
    [InlineData("next start", false)]
    [InlineData("nest start", false)]
    [InlineData("tsx src/index.ts", false)]
    [InlineData("vite build", false)]
    [InlineData("npm run build && node dist/index.js", false)]
    [InlineData("strapi start", false)]
    public void Tells_development_servers_from_production_ones(string script, bool dev)
    {
        using var repo = new TempRepo().With("package.json", """{ "scripts": { "dev": "nodemon index.js", "build": "tsc" } }""");
        var pkg = PackageJson.Load(Path.Combine(repo.Root, "package.json"));
        Assert.Equal(dev, ProjectAnalyzer.IsDevCommand(pkg, script));
    }

    [Theory]
    [InlineData("node ./bin/www", "bin/www")]
    [InlineData("node -r dotenv/config dist/main", "dist/main")]
    [InlineData("node --require ./tracing.js server.js", "server.js")]
    [InlineData("node --env-file .env server.js", "server.js")]
    [InlineData("nodemon --watch src --exec ts-node src/index.ts", "src/index.ts")]
    [InlineData("NODE_ENV=production node server.js", "server.js")]
    [InlineData("cross-env NODE_ENV=production node app.js", "app.js")]
    [InlineData("npm run build && node dist/index.js", "dist/index.js")]
    [InlineData("node dist/server.js | pino-pretty", "dist/server.js")]
    [InlineData("bun run src/index.ts", "src/index.ts")]
    [InlineData("node .", "lib/main.js")]
    [InlineData("npm run serve", "server.js")]
    [InlineData("next start", null)]
    public void Finds_the_file_a_script_runs(string script, string? file)
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "main": "./lib/main.js", "scripts": { "serve": "node server.js" } }""")
            .With("bin/www", "")
            .With(".env", "")
            .With("tracing.js", "");
        var pkg = PackageJson.Load(Path.Combine(repo.Root, "package.json"));
        Assert.Equal(file, ProjectAnalyzer.ScriptFile(repo.Root, pkg, script));
    }
}
