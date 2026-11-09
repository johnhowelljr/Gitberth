using Gitberth.Detection;

namespace Gitberth.Tests;

public class RustLanguageTests
{
    private const string AxumMain = """
        use axum::{routing::get, Router};

        #[tokio::main]
        async fn main() {
            let app = Router::new().route("/", get(|| async { "hi" }));
            let port = std::env::var("PORT").unwrap_or_else(|_| "3000".to_string());
            let listener = tokio::net::TcpListener::bind(format!("0.0.0.0:{port}")).await.unwrap();
            axum::serve(listener, app).await.unwrap();
        }
        """;

    private static DetectionResult Analyze(TempRepo repo, string? hint = null, string? appDir = null) =>
        new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint, AppDir: appDir));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static string Manifest(string name, string dependencies, string package = "") =>
        $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\nedition = \"2021\"\n{package}\n[dependencies]\n{dependencies}\n";

    private static TempRepo AxumApp() => new TempRepo()
        .With("Cargo.toml", Manifest("hello", "axum = \"0.8\"\ntokio = { version = \"1\", features = [\"full\"] }"))
        .With("src/main.rs", AxumMain);

    [Fact]
    public void Axum_app_builds_with_cargo_caches_and_runs_on_debian_slim()
    {
        using var repo = AxumApp();
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Rust, r.Kind);
        Assert.Equal("Rust (Axum)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Equal("", r.BuildContext);
        Assert.Equal(
        [
            "Found Cargo.toml; builds the hello binary",
            "Rust 1.99 (Gitberth's default)",
            "Listens on PORT, or 3000 when it isn't set (src/main.rs)",
            "The repo's other files (templates, static files, config…) are next to the binary in /app, the working directory, as in the repo",
        ], r.Evidence);
        Assert.Equal(["No Cargo.lock is committed, so each build may resolve different dependency versions."], r.Warnings);
        Assert.Equal("""
            FROM rust:1.99 AS build
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            # target/ is a cache kept between builds, so the binary is copied out of it in the same step.
            RUN --mount=type=cache,id=cargo-registry,target=/usr/local/cargo/registry \
                --mount=type=cache,id=cargo-git,target=/usr/local/cargo/git \
                --mount=type=cache,id=cargo-target-hello,target=/app/target \
                cargo build --release --bin hello \
                && mkdir -p /out && cp target/release/hello /out/
            # The repo's other files (templates/, static/, Rocket.toml...) stay, laid out as in the repo: apps often read them at run time.
            RUN find . -name '*.rs' -delete && find . -mindepth 1 -type d -empty -delete

            FROM debian:trixie-slim
            RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
            WORKDIR /app
            COPY --from=build /app/ ./
            COPY --from=build /out/hello ./
            ENV PORT=3000
            # gitberth:volumes
            EXPOSE 3000
            CMD ["/app/hello"]

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
    }

    [Fact]
    public void A_committed_cargo_lock_builds_locked()
    {
        using var repo = AxumApp().With("Cargo.lock", "version = 4\n\n[[package]]\nname = \"axum\"\nversion = \"0.8.4\"\n");
        var r = Analyze(repo);

        Assert.Contains("cargo build --release --locked --bin hello", r.Dockerfile);
        Assert.Contains("Builds with --locked, from Cargo.lock", r.Evidence);
        Assert.Empty(r.Warnings);
    }

    [Theory]
    [InlineData("let listener = tokio::net::TcpListener::bind(\"0.0.0.0:3000\").await.unwrap();", 3000)]
    [InlineData("let addr = SocketAddr::from(([0, 0, 0, 0], 8080));", 8080)]
    [InlineData("let addr = SocketAddr::new(IpAddr::V4(Ipv4Addr::UNSPECIFIED), 9000);", 9000)]
    [InlineData("const PORT: u16 = 4000;", 4000)]
    [InlineData("let port: u16 = env::var(\"PORT\").ok().and_then(|p| p.parse().ok()).unwrap_or(5000);", 5000)]
    [InlineData("let listener = tokio::net::TcpListener::bind(addr).await.unwrap();", 8080)]
    public void Finds_the_port_in_the_code(string line, int port)
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "axum = \"0.8\""))
            .With("src/main.rs", $"#[tokio::main]\nasync fn main() {{\n    {line}\n}}\n");
        var r = Analyze(repo);

        Assert.Equal(port, r.ContainerPort);
        Assert.Contains($"ENV PORT={port}\n", r.Dockerfile);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("can't be reached", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("actix-web = \"4\"", "HttpServer::new(|| App::new()).bind((\"127.0.0.1\", 8080))?.run().await", "Rust (Actix Web)", "127.0.0.1:8080")]
    [InlineData("warp = \"0.3\"", "warp::serve(routes).run(([127, 0, 0, 1], 3030)).await;", "Rust (Warp)", "127.0.0.1:3030")]
    [InlineData("poem = \"3\"", "Server::new(TcpListener::bind(\"localhost:3000\")).run(app).await", "Rust (Poem)", "localhost:3000")]
    public void Listening_on_localhost_warns(string dependency, string line, string title, string address)
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", dependency))
            .With("src/main.rs", $"async fn main() {{\n    {line}\n}}\n");
        var r = Analyze(repo);

        Assert.Equal(title, r.Title);
        Assert.Contains(r.Warnings, w => w.StartsWith($"src/main.rs listens on {address}, which can't be reached from outside the container.", StringComparison.Ordinal));
    }

    [Fact]
    public void Rocket_listens_on_every_interface_and_reads_PORT_through_ROCKET_PORT()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "rocket = \"0.5\""))
            .With("src/main.rs", "#[macro_use] extern crate rocket;\n\n#[launch]\nfn rocket() -> _ { rocket::build() }\n");
        var r = Analyze(repo);

        Assert.Equal("Rust (Rocket)", r.Title);
        Assert.Equal(8000, r.ContainerPort);
        Assert.Contains("ENV PORT=8000 ROCKET_ADDRESS=0.0.0.0 ROCKET_IP_HEADER=X-Forwarded-For\n", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"export ROCKET_PORT=${PORT:-8000} && exec /app/hello\"]", r.Dockerfile);
        Assert.Contains("Port 8000 (Rocket's default)", r.Evidence);

        repo.With("Rocket.toml", "[default]\nport = 8001\n\n[release]\nport = 9000\n");
        Assert.Equal(9000, Analyze(repo).ContainerPort);
        repo.With("src/main.rs", "#[launch]\nfn rocket() -> _ { rocket::custom(rocket::Config::figment().merge((\"port\", 7000))) }\n");
        Assert.Equal(7000, Analyze(repo).ContainerPort);
    }

    [Fact]
    public void Loco_starts_its_cli_binary_bound_to_every_interface()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", """
                [package]
                name = "myapp"
                version = "0.1.0"
                edition = "2021"

                [dependencies]
                loco-rs = { version = "0.16" }
                axum = "0.8"

                [[bin]]
                name = "myapp-cli"
                path = "src/bin/main.rs"
                required-features = []

                [[bin]]
                name = "tool"
                path = "src/bin/tool.rs"
                required-features = []
                """)
            .With("src/lib.rs", "pub mod app;\n")
            .With("src/bin/main.rs", "#[tokio::main]\nasync fn main() -> loco_rs::Result<()> { cli::main::<App, Migrator>().await }\n")
            .With("src/bin/tool.rs", "fn main() {}\n")
            .With("config/development.yaml", "database:\n  uri: {{ get_env(name=\"DATABASE_URL\", default=\"postgres://loco:loco@localhost:5432/myapp\") }}\n")
            .With("config/production.yaml", "server:\n  port: 5150\n");
        var r = Analyze(repo);

        Assert.Equal("Rust (Loco)", r.Title);
        Assert.Equal(5150, r.ContainerPort);
        Assert.Contains("--bin myapp-cli", r.Dockerfile);
        Assert.Contains("ENV PORT=5150 LOCO_ENV=production\n", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec /app/myapp-cli start --binding 0.0.0.0 --port ${PORT:-5150}\"]", r.Dockerfile);
        Assert.Contains("Found 2 binaries and builds myapp-cli. The others are tool. To run another one, switch the Dockerfile to Custom and change the binary in its cargo build line.", r.Warnings);
        Assert.Equal("used in config/development.yaml", r.EnvSuggestions.Single(s => s.Key == "DATABASE_URL").Source);
    }

    [Fact]
    public void A_workspace_builds_the_member_with_a_web_framework()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", "[workspace]\nmembers = [\"crates/*\", \"xtask\"]\nresolver = \"2\"\n\n[workspace.dependencies]\naxum = \"0.8\"\n")
            .With("crates/api/Cargo.toml", Manifest("api", "axum = { workspace = true }\ncore = { path = \"../core\" }"))
            .With("crates/api/src/main.rs", AxumMain)
            .With("crates/cli/Cargo.toml", Manifest("cli", "clap = \"4\""))
            .With("crates/cli/src/main.rs", "fn main() {}\n")
            .With("crates/core/Cargo.toml", Manifest("core", "serde = \"1\""))
            .With("crates/core/src/lib.rs", "")
            .With("xtask/Cargo.toml", Manifest("xtask", ""))
            .With("xtask/src/main.rs", "fn main() {}\n")
            .With("Cargo.lock", "version = 4\n");
        var r = Analyze(repo);

        Assert.Equal("Rust (Axum)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("cargo build --release --locked -p api --bin api \\\n", r.Dockerfile);
        Assert.Contains("Found Cargo.toml, a workspace; builds the api binary of api (crates/api/)", r.Evidence);
        Assert.Contains("Found 2 binaries and builds api. The others are cli. To run another one, switch the Dockerfile to Custom and change the binary in its cargo build line.", r.Warnings);
        Assert.Contains("Listens on PORT, or 3000 when it isn't set (crates/api/src/main.rs)", r.Evidence);
    }

    [Fact]
    public void A_workspace_member_chosen_as_the_folder_points_at_the_workspace_and_isnt_offered()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", "[workspace]\nmembers = [\"crates/api\", \"crates/core\"]\n")
            .With("crates/api/Cargo.toml", Manifest("api", "axum = \"0.8\""))
            .With("crates/api/src/main.rs", AxumMain)
            .With("crates/core/Cargo.toml", Manifest("core", ""))
            .With("crates/core/src/lib.rs", "");

        var member = Analyze(repo, appDir: "crates/api");
        Assert.Equal(ProjectKind.Unknown, member.Kind);
        Assert.Contains("crates/api/ is a member of the Cargo workspace in the repo root, and only builds from there. Set the app's folder to the repo root instead, and Gitberth builds the binary from there.", member.Warnings);

        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "" && a.Kind == ProjectKind.Rust);
        Assert.DoesNotContain(apps, a => a.Dir.StartsWith("crates", StringComparison.Ordinal));
    }

    [Fact]
    public void A_library_crate_isnt_offered()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("kit", "serde = \"1\""))
            .With("src/lib.rs", "pub fn hi() {}\n")
            .With("examples/demo.rs", "fn main() {}\n");

        Assert.Equal(ProjectKind.Unknown, Analyze(repo).Kind);
        Assert.Empty(new ProjectAnalyzer().FindApps(repo.Root));
    }

    [Fact]
    public void Binaries_in_src_bin_are_found_and_a_server_name_wins()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("toolkit", "axum = \"0.8\""))
            .With("src/lib.rs", "")
            .With("src/bin/import.rs", "fn main() {}\n")
            .With("src/bin/server.rs", "#[tokio::main]\nasync fn main() {\n    let listener = tokio::net::TcpListener::bind(\"0.0.0.0:4000\").await.unwrap();\n}\n");
        var r = Analyze(repo);

        Assert.Contains("--bin server \\\n", r.Dockerfile);
        Assert.Equal(4000, r.ContainerPort);
        Assert.Contains("Listens on port 4000 (src/bin/server.rs)", r.Evidence);
        Assert.Contains(r.Warnings, w => w.StartsWith("Found 2 binaries and builds server. The others are import.", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_run_picks_the_binary()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("shop", "axum = \"0.8\"", "default-run = \"web\""))
            .With("src/bin/api.rs", "fn main() {}\n")
            .With("src/bin/web.rs", "fn main() {}\n");
        Assert.Contains("--bin web \\\n", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void A_binary_named_like_a_top_level_folder_gets_another_name_in_the_image()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("web", "axum = \"0.8\""))
            .With("src/main.rs", AxumMain)
            .With("web/index.html", "<p>hi</p>");
        var r = Analyze(repo);

        Assert.Contains("cp target/release/web /out/", r.Dockerfile);
        Assert.Contains("COPY --from=build /out/web ./web-app\n", r.Dockerfile);
        Assert.Contains("CMD [\"/app/web-app\"]", r.Dockerfile);
    }

    [Theory]
    [InlineData("rust-toolchain.toml", "[toolchain]\nchannel = \"1.82.0\"\ncomponents = [\"clippy\"]\n", "", "1.82", "Rust 1.82 (rust-toolchain.toml: 1.82.0)")]
    [InlineData("rust-toolchain", "1.80\n", "", "1.80", "Rust 1.80 (rust-toolchain: 1.80)")]
    [InlineData("rust-toolchain.toml", "[toolchain]\nchannel = \"stable\"\n", "", "1.99", "Rust 1.99 (rust-toolchain.toml: stable)")]
    [InlineData("rust-toolchain.toml", "[toolchain]\nchannel = \"nightly-2025-06-01\"\n", "", "1.99", "Rust nightly-2025-06-01 (rust-toolchain.toml: nightly-2025-06-01), which rustup installs during the build, on the rust:1.99 image")]
    [InlineData(null, null, "rust-version = \"1.70\"", "1.99", "Rust 1.99 (Gitberth's default; Cargo.toml's rust-version 1.70 is the oldest it supports)")]
    [InlineData(null, null, "rust-version = \"1.105\"", "1.105", "Rust 1.105 (rust-version 1.105 in Cargo.toml)")]
    public void The_rust_image_follows_the_toolchain_file_or_a_newer_rust_version(string? file, string? content, string package, string tag, string evidence)
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "axum = \"0.8\"", package))
            .With("src/main.rs", AxumMain);
        if (file is not null) repo.With(file, content!);
        var r = Analyze(repo);

        Assert.StartsWith($"FROM rust:{tag} AS build\n", r.Dockerfile);
        Assert.Contains(evidence, r.Evidence);
    }

    [Fact]
    public void A_members_rust_version_can_come_from_the_workspace()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", "[workspace]\nmembers = [\"app\"]\n\n[workspace.package]\nrust-version = \"1.101\"\n")
            .With("app/Cargo.toml", "[package]\nname = \"app\"\nversion = \"0.1.0\"\nrust-version.workspace = true\n\n[dependencies]\naxum = \"0.8\"\n")
            .With("app/src/main.rs", AxumMain);
        Assert.StartsWith("FROM rust:1.101 AS build\n", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void C_libraries_linked_at_run_time_are_installed_from_cargo_lock()
    {
        using var repo = AxumApp().With("Cargo.lock", """
            version = 4

            [[package]]
            name = "openssl-sys"
            version = "0.9.104"

            [[package]]
            name = "pq-sys"
            version = "0.6.3"

            [[package]]
            name = "libsqlite3-sys"
            version = "0.30.1"
            """);
        var r = Analyze(repo);

        Assert.Contains("apt-get install -y --no-install-recommends ca-certificates libpq5 libsqlite3-0 && rm", r.Dockerfile);
        Assert.Contains("Links libpq (through pq-sys), so the image gets libpq5", r.Evidence);
    }

    [Theory]
    [InlineData("diesel = { version = \"2\", features = [\"postgres\", \"mysql\"] }", "ca-certificates libpq5 libmariadb3 &&")]
    [InlineData("rusqlite = \"0.32\"", "ca-certificates libsqlite3-0 &&")]
    [InlineData("rusqlite = { version = \"0.32\", features = [\"bundled\"] }", "ca-certificates &&")]
    public void Without_a_lockfile_c_libraries_come_from_the_dependencies(string dependency, string install)
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "axum = \"0.8\"\n" + dependency))
            .With("src/main.rs", AxumMain);
        Assert.Contains($"apt-get install -y --no-install-recommends {install}", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void A_crate_that_serves_nothing_is_a_worker_and_gives_way_to_a_front_end()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("jobs", "tokio = { version = \"1\", features = [\"full\"] }"))
            .With("src/main.rs", "#[tokio::main]\nasync fn main() {\n    loop { tokio::time::sleep(std::time::Duration::from_secs(60)).await; }\n}\n");
        var r = Analyze(repo);
        Assert.Equal("Rust worker", r.Title);
        Assert.Null(r.ContainerPort);
        Assert.DoesNotContain("EXPOSE", r.Dockerfile);

        repo.With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        Assert.Equal(ProjectKind.NodeSpa, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Rust, Analyze(repo, "rust").Kind);
    }

    [Fact]
    public void A_build_target_from_cargo_config_is_added_and_copied_from_its_folder()
    {
        using var repo = AxumApp().With(".cargo/config.toml", "[build]\ntarget = \"x86_64-unknown-linux-musl\"\n");
        var r = Analyze(repo);

        Assert.Contains("COPY . .\nRUN rustup target add x86_64-unknown-linux-musl\n", r.Dockerfile);
        Assert.Contains("cp target/x86_64-unknown-linux-musl/release/hello /out/", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("musl-tools", StringComparison.Ordinal));
    }

    [Fact]
    public void A_renamed_dependency_counts_as_its_package()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "web = { package = \"actix-web\", version = \"4\" }"))
            .With("src/main.rs", "fn main() { HttpServer::new(|| App::new()).bind((\"0.0.0.0\", 8080)) }\n");
        Assert.Equal("Rust (Actix Web)", Analyze(repo).Title);
    }

    [Fact]
    public void Shuttle_and_leptos_apps_say_what_they_need()
    {
        using var shuttle = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "axum = \"0.8\"\nshuttle-runtime = \"0.49\""))
            .With("src/main.rs", "#[shuttle_runtime::main]\nasync fn main() -> shuttle_axum::ShuttleAxum { Ok(router.into()) }\n");
        Assert.Contains(Analyze(shuttle).Warnings, w => w.StartsWith("The app starts through #[shuttle_runtime::main]", StringComparison.Ordinal));

        using var leptos = new TempRepo()
            .With("Cargo.toml", Manifest("site", "leptos = \"0.8\"\nleptos_axum = \"0.8\"\naxum = \"0.8\"") + "\n[package.metadata.leptos]\nsite-addr = \"127.0.0.1:3000\"\n")
            .With("src/main.rs", "#[tokio::main]\nasync fn main() {\n    let listener = tokio::net::TcpListener::bind(&addr).await.unwrap();\n}\n");
        var r = Analyze(leptos);
        Assert.Equal("Rust (Leptos)", r.Title);
        Assert.Equal(3000, r.ContainerPort);
        Assert.Contains("ENV PORT=3000 LEPTOS_SITE_ADDR=0.0.0.0:3000\n", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("Leptos apps build with cargo-leptos", StringComparison.Ordinal));
    }

    [Fact]
    public void Suggests_the_variables_the_code_reads()
    {
        using var repo = new TempRepo()
            .With("Cargo.toml", Manifest("hello", "axum = \"0.8\""))
            .With("src/main.rs", """
                use std::env;

                #[derive(serde::Deserialize)]
                struct Config {
                    database_url: String,
                    #[serde(default)]
                    pub max_connections: u32,
                }

                fn main() {
                    let _ = env::var("SESSION_SECRET");
                    let _ = std::env::var_os("UPLOAD_DIR");
                    let _ = dotenvy::var("SMTP_URL");
                    let _ = env!("GIT_SHA");
                    let _ = option_env!("CARGO_PKG_VERSION");
                    let _ = envy::prefixed("APP_").from_env::<Config>().unwrap();
                    let _ = env::var("PORT");
                }
                """)
            .With("src/bin/report.rs", "fn main() { let _ = std::env::var(\"REPORT_EMAIL\"); }\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.True(env["SESSION_SECRET"].IsSecret);
        Assert.Equal("used in src/main.rs", env["UPLOAD_DIR"].Source);
        Assert.Contains("SMTP_URL", env.Keys);
        Assert.Equal("read while it compiles, by env!() in src/main.rs: mark it Build-time", env["GIT_SHA"].Source);
        Assert.Contains("APP_DATABASE_URL", env.Keys);
        Assert.Contains("APP_MAX_CONNECTIONS", env.Keys);
        Assert.Equal("used in src/bin/report.rs", env["REPORT_EMAIL"].Source);
        Assert.DoesNotContain("CARGO_PKG_VERSION", env.Keys);
        Assert.DoesNotContain("PORT", env.Keys);
    }

    [Fact]
    public void Reads_the_toml_cargo_uses()
    {
        var doc = RustToml.Parse(""""
            # A comment.
            [package]
            name = "app" # trailing comment
            "quoted key" = 'C:\literal'
            features.default = ["a", "b"]
            this line = is broken
            description = """
            multi
            line"""
            empty = []

            [dependencies]
            serde = { version = "1", features = ["derive"] }

            [dependencies.diesel]
            version = "2"
            features = [
                "postgres", # comment
                "r2d2",
            ]

            [[bin]]
            name = "a"

            [[bin]]
            name = "b"
            path = "src/bin/b.rs"

            [target.'cfg(unix)'.dependencies]
            nix = "0.29"
            """");

        Assert.Equal("app", RustToml.Str(doc, "package", "name"));
        Assert.Equal(@"C:\literal", RustToml.Str(doc, "package", "quoted key"));
        Assert.Equal(["a", "b"], RustToml.Strings(RustToml.Get(doc, "package", "features", "default")));
        Assert.Equal("multi\nline", RustToml.Str(doc, "package", "description"));
        Assert.Empty(RustToml.Strings(RustToml.Get(doc, "package", "empty")));
        Assert.Equal(["derive"], RustToml.Strings(RustToml.Get(doc, "dependencies", "serde", "features")));
        Assert.Equal(["postgres", "r2d2"], RustToml.Strings(RustToml.Get(doc, "dependencies", "diesel", "features")));
        Assert.Equal(["a", "b"], RustToml.Tables(RustToml.Get(doc, "bin")).Select(t => RustToml.Str(t, "name")));
        Assert.Equal("0.29", RustToml.Str(doc, "target", "cfg(unix)", "dependencies", "nix"));
        Assert.Null(RustToml.Get(doc, "package", "this"));
    }
}
