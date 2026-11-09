using Gitberth.Detection;

namespace Gitberth.Tests;

public class GoLanguageTests
{
    private const string NetHttpMain = """
        package main

        import (
        	"fmt"
        	"net/http"
        	"os"
        )

        func main() {
        	port := os.Getenv("PORT")
        	if port == "" {
        		port = "9000"
        	}
        	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) { fmt.Fprintln(w, "hi") })
        	http.ListenAndServe(":"+port, nil)
        }
        """;

    private static DetectionResult Analyze(TempRepo repo, string? hint = null, string? appDir = null) =>
        new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint, AppDir: appDir));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static TempRepo GoApp(string goMod = "module example.com/hello\n\ngo 1.26\n", string main = NetHttpMain) =>
        new TempRepo().With("go.mod", goMod).With("main.go", main);

    /// <summary>A main.go that imports <paramref name="module"/> and runs <paramref name="body"/>.</summary>
    private static string MainUsing(string module, string body) =>
        $"package main\n\nimport (\n\t\"net\"\n\t\"net/http\"\n\t\"{module}\"\n)\n\nfunc main() {{\n\t{body}\n}}\n";

    [Fact]
    public void Root_main_package_builds_a_static_binary_that_runs_next_to_the_repos_files()
    {
        using var repo = GoApp();
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Go, r.Kind);
        Assert.Equal("Go (net/http)", r.Title);
        Assert.Equal(9000, r.ContainerPort);
        Assert.Equal("", r.BuildContext);
        Assert.Equal(GoDockerfiles.Ignore, r.DockerIgnore);
        Assert.Equal(
        [
            "Found go.mod (module example.com/hello); builds the main package in the module root",
            "Go 1.26 (go.mod's go line: 1.26)",
            "Uses only the standard library, so there are no modules to download",
            "A static binary (CGO_ENABLED=0), on Alpine with CA certificates and time zones",
            "Listens on PORT, or 9000 when it isn't set (main.go)",
            "The repo's other files (templates, static files, migrations…) are next to the binary in /app, the working directory, as in the repo",
        ], r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Equal("""
            FROM golang:1.26 AS build
            WORKDIR /app
            # Go downloads the toolchain go.mod or a module asks for, when it's newer than this image's.
            ENV GOTOOLCHAIN=auto
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=gomod,target=/go/pkg/mod \
                --mount=type=cache,id=gobuild,target=/root/.cache/go-build \
                CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/hello .
            # The repo's other files (templates/, static/, migrations/...) stay, laid out as in the repo: apps often read them at run time.
            RUN find . -name '*.go' -delete && find . -mindepth 1 -type d -empty -delete

            FROM alpine:3
            RUN apk add --no-cache ca-certificates tzdata
            WORKDIR /app
            COPY --from=build /app/ ./
            COPY --from=build /out/hello ./
            ENV PORT=9000
            # gitberth:volumes
            EXPOSE 9000
            CMD ["/app/hello"]

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
    }

    [Fact]
    public void Modules_download_first_from_go_sum_in_a_layer_of_their_own()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.26\n\nrequire github.com/go-chi/chi/v5 v5.2.1\n",
                MainUsing("github.com/go-chi/chi/v5", "r := chi.NewRouter()\n\thttp.ListenAndServe(\":3333\", r)"))
            .With("go.sum", "github.com/go-chi/chi/v5 v5.2.1 h1:x\n");
        var r = Analyze(repo);

        Assert.Equal("Go (Chi)", r.Title);
        Assert.Equal(3333, r.ContainerPort);
        Assert.Contains("""
            COPY go.mod go.sum ./
            RUN --mount=type=cache,id=gomod,target=/go/pkg/mod go mod download
            COPY . .
            # gitberth:build-args
            """.ReplaceLineEndings("\n"), r.Dockerfile);
        Assert.DoesNotContain("go mod tidy", r.Dockerfile);
        Assert.Contains("The modules download from go.sum first, in a layer that's reused until go.mod or go.sum change", r.Evidence);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Without_go_sum_go_mod_tidy_writes_it_and_a_warning_says_to_commit_it()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.26\n\nrequire github.com/gin-gonic/gin v1.10.0\n",
            MainUsing("github.com/gin-gonic/gin", "r := gin.Default()\n\tr.Run()"));
        var r = Analyze(repo);

        Assert.Contains("    go mod tidy && CGO_ENABLED=0 go build -trimpath", r.Dockerfile);
        Assert.DoesNotContain("go mod download", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.StartsWith("go.sum isn't committed, so the build runs go mod tidy", StringComparison.Ordinal));
    }

    [Fact]
    public void A_vendor_folder_builds_with_mod_vendor_and_stays_out_of_the_image()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.26\n\nrequire github.com/go-chi/chi/v5 v5.2.1\n",
                MainUsing("github.com/go-chi/chi/v5", "http.ListenAndServe(\":8080\", chi.NewRouter())"))
            .With("vendor/modules.txt", "# github.com/go-chi/chi/v5 v5.2.1\n");
        var r = Analyze(repo);

        Assert.Contains("CGO_ENABLED=0 go build -mod=vendor -trimpath", r.Dockerfile);
        Assert.Contains("RUN rm -rf vendor && find . -name '*.go' -delete", r.Dockerfile);
        Assert.DoesNotContain("go mod download", r.Dockerfile);
        Assert.Contains("The modules come from vendor/", r.Evidence);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Under_cmd_the_main_package_named_like_the_module_wins_and_the_others_are_listed()
    {
        using var repo = new TempRepo()
            .With("go.mod", "module github.com/acme/shop/v2\n\ngo 1.26\n")
            .With("cmd/migrate/main.go", "package main\n\nfunc main() {}\n")
            .With("cmd/shop/main.go", NetHttpMain)
            .With("cmd/worker/main.go", "package main\n\nimport \"net/http\"\n\nfunc main() { http.ListenAndServe(\":7000\", nil) }\n")
            .With("internal/store/store.go", "package store\n");
        var r = Analyze(repo);

        Assert.Contains("-o /out/shop ./cmd/shop\n", r.Dockerfile);
        Assert.Contains("CMD [\"/app/shop\"]", r.Dockerfile);
        Assert.Contains("Found go.mod (module github.com/acme/shop/v2); builds the main package in cmd/shop/", r.Evidence);
        Assert.Contains("Found 3 main packages and builds the one in cmd/shop/. The others are in cmd/migrate/, cmd/worker/. "
            + "To run another one, switch the Dockerfile to Custom and change the path at the end of its go build line.", r.Warnings);
        Assert.Equal(9000, r.ContainerPort);
    }

    [Fact]
    public void Under_cmd_one_that_serves_http_wins_over_a_command_line_tool()
    {
        using var repo = new TempRepo()
            .With("go.mod", "module example.com/platform\n\ngo 1.26\n")
            .With("cmd/admin-cli/main.go", "package main\n\nimport \"fmt\"\n\nfunc main() { fmt.Println(\"hi\") }\n")
            .With("cmd/gateway/main.go", "package main\n\nimport \"net/http\"\n\nfunc main() { http.ListenAndServe(\":8081\", nil) }\n");
        var r = Analyze(repo);

        Assert.Contains("./cmd/gateway\n", r.Dockerfile);
        Assert.Equal(8081, r.ContainerPort);
    }

    [Fact]
    public void A_main_package_elsewhere_is_built_and_its_binary_avoids_the_folder_it_comes_from()
    {
        // The binary goes in /app next to the repo's files, where a server/ folder is too.
        using var repo = new TempRepo()
            .With("go.mod", "module example.com/app\n\ngo 1.26\n")
            .With("server/main.go", NetHttpMain)
            .With("server/templates/home.html", "<p>hi</p>");
        var r = Analyze(repo);

        Assert.Contains("-o /out/server-app ./server\n", r.Dockerfile);
        Assert.Contains("COPY --from=build /out/server-app ./", r.Dockerfile);
        Assert.Contains("CMD [\"/app/server-app\"]", r.Dockerfile);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_module_without_a_main_package_is_a_library_and_isnt_offered()
    {
        using var repo = new TempRepo()
            .With("go.mod", "module github.com/acme/kit\n\ngo 1.26\n")
            .With("kit.go", "package kit\n")
            .With("examples/hello/main.go", "package main\n\nimport \"net/http\"\n\nfunc main() { http.ListenAndServe(\":8080\", nil) }\n")
            .With("gen.go", "//go:build ignore\n\npackage main\n\nfunc main() {}\n");

        Assert.NotEqual(ProjectKind.Go, Analyze(repo).Kind);
        Assert.DoesNotContain(new ProjectAnalyzer().FindApps(repo.Root), c => c.Kind == ProjectKind.Go);
    }

    [Theory]
    [InlineData("go 1.24\ntoolchain go1.26.2\n", "1.26", "Go 1.26 (go.mod's toolchain line: go1.26.2)")]
    [InlineData("go 1.27.1\ntoolchain go1.26.0\n", "1.27", "Go 1.27 (go.mod's go line: 1.27.1)")]
    [InlineData("go 1.26rc1\n", "1.26", "Go 1.26 (go.mod's go line: 1.26rc1)")]
    [InlineData("", "1.27", "Go 1.27 (Gitberth's default; go.mod has no go line)")]
    public void The_golang_image_follows_the_go_or_toolchain_line(string lines, string tag, string evidence)
    {
        using var repo = GoApp($"module example.com/hello\n\n{lines}");
        var r = Analyze(repo);

        Assert.StartsWith($"FROM golang:{tag} AS build\n", r.Dockerfile);
        Assert.Contains(evidence, r.Evidence);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_go_release_newer_than_gitberth_knows_starts_from_the_newest_image_and_fetches_its_toolchain()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.29.0\n");
        var r = Analyze(repo);

        Assert.StartsWith("FROM golang:1.27 AS build\n", r.Dockerfile);
        Assert.Contains("ENV GOTOOLCHAIN=auto\n", r.Dockerfile);
        Assert.Contains("go.mod asks for Go 1.29, which is newer than Gitberth knows. The build starts from golang:1.27, and Go downloads the 1.29 toolchain itself.", r.Warnings);
    }

    [Fact]
    public void An_old_go_release_still_builds_with_its_own_image_and_a_warning()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.22\n");
        var r = Analyze(repo);

        Assert.StartsWith("FROM golang:1.22 AS build\n", r.Dockerfile);
        Assert.Contains("Go 1.22 no longer gets security fixes; only Go 1.26 and 1.27 do. Raise the go line in go.mod to build with a supported release.", r.Warnings);
    }

    [Theory]
    [InlineData("github.com/gin-gonic/gin", "r := gin.Default()\n\tr.Run()", "Go (Gin)", 8080)]
    [InlineData("github.com/gin-gonic/gin", "r := gin.Default()\n\tr.Run(\":9090\")", "Go (Gin)", 9090)]
    [InlineData("github.com/labstack/echo/v4", "e := echo.New()\n\te.Logger.Fatal(e.Start(\":1323\"))", "Go (Echo)", 1323)]
    [InlineData("github.com/labstack/echo/v5", "e := echo.New()\n\te.Start(\"0.0.0.0:8081\")", "Go (Echo)", 8081)]
    [InlineData("github.com/gofiber/fiber/v2", "app := fiber.New()\n\tapp.Listen(\":3000\")", "Go (Fiber)", 3000)]
    [InlineData("github.com/gofiber/fiber/v3", "app := fiber.New()\n\tapp.Listen(addr)", "Go (Fiber)", 3000)]
    [InlineData("github.com/go-chi/chi/v5", "r := chi.NewRouter()\n\thttp.ListenAndServe(\":3333\", r)", "Go (Chi)", 3333)]
    [InlineData("github.com/gorilla/mux", "r := mux.NewRouter()\n\tsrv := &http.Server{Addr: \"0.0.0.0:8000\", Handler: r}\n\tsrv.ListenAndServe()", "Go (Gorilla mux)", 8000)]
    [InlineData("github.com/cloudwego/hertz/pkg/app/server", "h := server.Default()\n\th.Spin()", "Go (Hertz)", 8888)]
    [InlineData("github.com/julienschmidt/httprouter", "router := httprouter.New()\n\thttp.ListenAndServe(\":8080\", router)", "Go (httprouter)", 8080)]
    [InlineData("github.com/kataras/iris/v12", "app := iris.New()\n\tapp.Listen(\":8080\")", "Go (Iris)", 8080)]
    [InlineData("github.com/gobuffalo/buffalo", "app := buffalo.New(buffalo.Options{})\n\tapp.Serve()", "Go (Buffalo)", 3000)]
    [InlineData("google.golang.org/grpc", "lis, _ := net.Listen(\"tcp\", \":50051\")\n\ts := grpc.NewServer()\n\ts.Serve(lis)", "Go (gRPC)", 50051)]
    [InlineData("fmt", "http.ListenAndServe(\":8080\", nil)", "Go (net/http)", 8080)]
    public void Names_the_framework_and_finds_its_port(string module, string body, string title, int port)
    {
        using var repo = GoApp(main: MainUsing(module, body));
        var r = Analyze(repo);

        Assert.Equal(title, r.Title);
        Assert.Equal(port, r.ContainerPort);
        Assert.Contains($"ENV PORT={port}", r.Dockerfile);
        Assert.Contains($"EXPOSE {port}\n", r.Dockerfile);
    }

    [Fact]
    public void Beego_listens_where_its_app_conf_says()
    {
        using var repo = GoApp(main: MainUsing("github.com/beego/beego/v2/server/web", "web.Run()"))
            .With("conf/app.conf", "appname = shop\nhttpport = 8081\nrunmode = prod\n");
        var r = Analyze(repo);

        Assert.Equal("Go (Beego)", r.Title);
        Assert.Equal(8081, r.ContainerPort);
        Assert.Contains("Listens on port 8081 (httpport in conf/app.conf)", r.Evidence);
    }

    [Fact]
    public void Gin_runs_in_release_mode_and_buffalo_in_production_on_every_interface()
    {
        using var gin = GoApp(main: MainUsing("github.com/gin-gonic/gin", "gin.Default().Run()"));
        Assert.Contains("ENV PORT=8080 GIN_MODE=release\n", Analyze(gin).Dockerfile);

        using var buffalo = GoApp(main: MainUsing("github.com/gobuffalo/buffalo", "buffalo.New(buffalo.Options{}).Serve()"));
        var r = Analyze(buffalo);
        Assert.Contains("ENV PORT=3000 GO_ENV=production ADDR=0.0.0.0\n", r.Dockerfile);
        Assert.Contains("Runs Buffalo in production mode on every interface (GO_ENV=production, ADDR=0.0.0.0)", r.Evidence);
    }

    [Fact]
    public void PocketBase_serves_on_every_interface_and_warns_that_pb_data_needs_a_volume()
    {
        using var repo = GoApp(main: MainUsing("github.com/pocketbase/pocketbase", "app := pocketbase.New()\n\tapp.Start()"));
        var r = Analyze(repo);

        Assert.Equal("Go (PocketBase)", r.Title);
        Assert.Equal(8090, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec /app/hello serve --http=0.0.0.0:${PORT:-8090}\"]", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("Add a volume at /app/pb_data", StringComparison.Ordinal));
    }

    [Fact]
    public void A_listen_address_flag_is_given_PORT_on_every_interface()
    {
        using var repo = GoApp(main: """
            package main

            import (
            	"flag"
            	"net/http"
            )

            var addr = flag.String("addr", "localhost:8080", "http service address")

            func main() {
            	flag.Parse()
            	http.ListenAndServe(*addr, nil)
            }
            """);
        var r = Analyze(repo);

        Assert.Equal(8080, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec /app/hello --addr=:${PORT:-8080}\"]", r.Dockerfile);
        Assert.Contains("Its --addr flag sets where it listens (main.go), so it starts with `hello --addr=:$PORT`", r.Evidence);
        // Gitberth passes the address itself, so its localhost default doesn't matter.
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_port_flag_is_given_PORT()
    {
        using var repo = GoApp(main: "package main\n\nimport (\n\t\"flag\"\n\t\"fmt\"\n\t\"net/http\"\n)\n\nfunc main() {\n\tport := flag.Int(\"port\", 4000, \"port\")\n\tflag.Parse()\n\thttp.ListenAndServe(fmt.Sprintf(\":%d\", *port), nil)\n}\n");
        var r = Analyze(repo);

        Assert.Equal(4000, r.ContainerPort);
        Assert.Contains("\"exec /app/hello --port=${PORT:-4000}\"", r.Dockerfile);
    }

    [Fact]
    public void A_cobra_serve_command_is_started_with_its_own_flags()
    {
        using var repo = new TempRepo()
            .With("go.mod", "module example.com/app\n\ngo 1.26\n\nrequire github.com/spf13/cobra v1.8.0\n")
            .With("main.go", "package main\n\nimport \"example.com/app/cmd\"\n\nfunc main() { cmd.Execute() }\n")
            .With("cmd/serve.go", """
                package cmd

                import (
                	"net/http"

                	"github.com/spf13/cobra"
                )

                var addr string

                var serveCmd = &cobra.Command{
                	Use:   "serve",
                	Short: "Start the web server",
                	Run:   func(cmd *cobra.Command, args []string) { http.ListenAndServe(addr, nil) },
                }

                func init() {
                	serveCmd.Flags().StringVarP(&addr, "addr", "a", ":7000", "address to listen on")
                	rootCmd.AddCommand(serveCmd)
                }
                """);
        var r = Analyze(repo);

        Assert.Equal(7000, r.ContainerPort);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec /app/app serve --addr=:${PORT:-7000}\"]", r.Dockerfile);
        Assert.Contains("Starts its `serve` command (cmd/serve.go)", r.Evidence);
    }

    [Theory]
    [InlineData("http.ListenAndServe(\"127.0.0.1:8080\", nil)", "main.go listens on 127.0.0.1:8080")]
    [InlineData("http.ListenAndServe(\"localhost:\"+port, nil)", "main.go listens on localhost")]
    [InlineData("http.ListenAndServe(fmt.Sprintf(\"127.0.0.1:%s\", port), nil)", "main.go listens on 127.0.0.1")]
    public void Listening_on_localhost_warns(string call, string warning)
    {
        using var repo = GoApp(main: $"package main\n\nimport (\n\t\"fmt\"\n\t\"net/http\"\n\t\"os\"\n)\n\nfunc main() {{\n\tport := os.Getenv(\"PORT\")\n\t{call}\n}}\n");
        var r = Analyze(repo);

        Assert.Contains(r.Warnings, w => w.StartsWith(warning + ", which can't be reached from outside the container.", StringComparison.Ordinal));
    }

    [Fact]
    public void Cgo_builds_on_debian_with_glibc()
    {
        using var repo = GoApp(main: NetHttpMain).With("native.go", "package main\n\n// #include <stdio.h>\nimport \"C\"\n");
        var r = Analyze(repo);

        Assert.Contains("CGO_ENABLED=1 go build", r.Dockerfile);
        Assert.Contains("FROM debian:trixie-slim\nRUN apt-get update && apt-get install -y --no-install-recommends ca-certificates tzdata && rm -rf /var/lib/apt/lists/*\n", r.Dockerfile);
        Assert.Contains("Built with cgo because native.go imports \"C\", so it runs on Debian slim, which has the C library it links to", r.Evidence);
    }

    [Fact]
    public void Go_sqlite3_needs_cgo_even_as_an_indirect_requirement()
    {
        using var repo = GoApp("module example.com/hello\n\ngo 1.26\n\nrequire (\n\tgorm.io/driver/sqlite v1.5.0\n\tgithub.com/mattn/go-sqlite3 v1.14.22 // indirect\n)\n")
            .With("go.sum", "x\n");
        var r = Analyze(repo);

        Assert.Contains("CGO_ENABLED=1 go build", r.Dockerfile);
        Assert.Contains(r.Evidence, e => e.StartsWith("Built with cgo because go-sqlite3 is C code underneath", StringComparison.Ordinal));
    }

    [Fact]
    public void Cgo_libraries_the_image_may_lack_are_named()
    {
        using var repo = GoApp(main: NetHttpMain).With("image.go", "package main\n\n// #cgo pkg-config: vips\n// #cgo LDFLAGS: -lheif -lm\n// #include <vips/vips.h>\nimport \"C\"\n");
        var r = Analyze(repo);

        Assert.Contains("The cgo code links vips, heif, which the images may not have. If the build or the start fails, install them in a custom Dockerfile.", r.Warnings);
    }

    [Fact]
    public void An_embedded_front_end_that_isnt_committed_is_named_with_what_builds_it()
    {
        using var repo = GoApp()
            .With("web/embed.go", "package web\n\nimport \"embed\"\n\n//go:embed all:dist\nvar Dist embed.FS\n")
            .With("web/package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("assets/assets.go", "package assets\n\nimport \"embed\"\n\n//go:embed templates/*.html \"static files\"\nvar FS embed.FS\n")
            .With("assets/templates/home.html", "<p>hi</p>")
            .With("assets/static files/app.css", "")
            .With("version/version.go", "package version\n\nimport _ \"embed\"\n\n//go:embed version.txt\nvar Version string\n")
            .With("version/version.txt", "1.0.0");
        var r = Analyze(repo);

        // dist/ is left out of Gitberth's file index, so what an embed names is checked on disk.
        Assert.Equal(["web/embed.go embeds web/dist, which isn't in the repo; web/package.json probably builds it. Gitberth only builds the Go code, so the build fails until it's there: commit it, or switch the Dockerfile to Custom and build it before go build."], r.Warnings);

        repo.With("web/dist/index.html", "<div id=app></div>");
        Assert.Empty(Analyze(repo).Warnings);
    }

    [Fact]
    public void A_worker_has_no_port_and_gives_way_to_a_front_end_in_the_same_folder()
    {
        using var repo = new TempRepo()
            .With("go.mod", "module example.com/jobs\n\ngo 1.26\n")
            .With("main.go", "package main\n\nimport (\n\t\"log\"\n\t\"time\"\n)\n\nfunc main() {\n\tfor {\n\t\tlog.Println(\"tick\")\n\t\ttime.Sleep(time.Minute)\n\t}\n}\n");
        var worker = Analyze(repo);
        Assert.Equal("Go worker", worker.Title);
        Assert.Null(worker.ContainerPort);
        Assert.DoesNotContain("EXPOSE", worker.Dockerfile);
        Assert.DoesNotContain("ENV PORT", worker.Dockerfile);

        repo.With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""");
        Assert.Equal(ProjectKind.NodeSpa, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Go, Analyze(repo, "go").Kind);
    }

    [Fact]
    public void A_go_server_wins_over_a_package_json_that_only_builds_assets()
    {
        using var repo = GoApp()
            .With("package.json", """{ "scripts": { "build": "tailwindcss -i in.css -o static/app.css" }, "devDependencies": { "tailwindcss": "^4.0.0" } }""");
        Assert.Equal(ProjectKind.Go, Analyze(repo).Kind);
    }

    [Fact]
    public void A_node_server_in_the_same_folder_wins_and_both_are_offered()
    {
        using var repo = GoApp()
            .With("package.json", """{ "scripts": { "start": "node server.js" }, "dependencies": { "express": "^5.0.0" } }""")
            .With("server.js", "");

        Assert.Equal(ProjectKind.NodeServer, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Go, Analyze(repo, "go").Kind);
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Contains(apps, a => a.Dir == "" && a.Kind == ProjectKind.NodeServer);
        Assert.Contains(apps, a => a.Dir == "" && a.Hint == "go" && a.Kind == ProjectKind.Go && a.Title == "Go (net/http)");
    }

    [Fact]
    public void A_go_service_in_a_sub_folder_is_offered_and_built_from_its_folder()
    {
        using var repo = new TempRepo()
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("api/go.mod", "module example.com/api\n\ngo 1.26\n")
            .With("api/main.go", NetHttpMain);

        Assert.Contains(new ProjectAnalyzer().FindApps(repo.Root), a => a.Dir == "api" && a.Kind == ProjectKind.Go);
        var r = Analyze(repo, appDir: "api");
        Assert.Equal(ProjectKind.Go, r.Kind);
        Assert.Equal("api", r.BuildContext);
    }

    [Fact]
    public void A_main_package_chosen_as_the_folder_points_at_its_module()
    {
        using var repo = GoApp().With("cmd/server/main.go", NetHttpMain);
        var r = Analyze(repo, appDir: "cmd/server");

        Assert.Equal(ProjectKind.Unknown, r.Kind);
        Assert.Contains("cmd/server/ is a main package of the Go module in the repo root, and only builds from there. Set the app's folder to the repo root instead, and Gitberth builds the module's main package.", r.Warnings);
    }

    [Fact]
    public void A_local_replacement_outside_the_folder_warns_and_skips_the_download_layer()
    {
        using var repo = GoApp("module example.com/api\n\ngo 1.26\n\nrequire example.com/shared v0.0.0\n\nreplace example.com/shared => ../shared\n")
            .With("go.sum", "x\n");
        var r = Analyze(repo);

        Assert.DoesNotContain("go mod download", r.Dockerfile);
        Assert.Contains("go.mod replaces a module with ../shared, outside the app's folder, so the build can't see it. Vendor the modules (go mod vendor), or build from a folder that holds both.", r.Warnings);
    }

    [Fact]
    public void Suggests_the_variables_the_code_reads()
    {
        using var repo = GoApp(main: """
            package main

            import (
            	"os"

            	"github.com/spf13/viper"
            )

            type Config struct {
            	SMTPHost string `env:"SMTP_HOST" envDefault:"localhost"`
            	Region   string `envconfig:"AWS_REGION" default:"eu-west-1"`
            	Token    string `env:"GITHUB_TOKEN,required"`
            }

            func main() {
            	_ = os.Getenv("DATABASE_URL")
            	_, _ = os.LookupEnv("REDIS_URL")
            	_ = getEnv("SENTRY_DSN", "")
            	_ = os.Getenv("PORT")
            	os.Setenv("SET_BY_ITSELF", "1")
            	viper.BindEnv("db_name")
            	viper.BindEnv("stripe.key", "STRIPE_SECRET_KEY")
            }
            """).With("main_test.go", "package main\n\nimport \"os\"\n\nfunc TestX() { os.Getenv(\"ONLY_IN_TESTS\") }\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.Equal("used in main.go", env["DATABASE_URL"].Source);
        foreach (var key in new[] { "REDIS_URL", "SENTRY_DSN", "SMTP_HOST", "AWS_REGION", "GITHUB_TOKEN", "DB_NAME", "STRIPE_SECRET_KEY" }) Assert.Contains(key, env.Keys);
        Assert.True(env["GITHUB_TOKEN"].IsSecret);
        Assert.Equal("", env["SMTP_HOST"].Value);
        Assert.DoesNotContain("PORT", env.Keys);
        Assert.DoesNotContain("SET_BY_ITSELF", env.Keys);
        Assert.DoesNotContain("ONLY_IN_TESTS", env.Keys);
    }

    [Fact]
    public void Reads_go_mod()
    {
        var mod = GoMod.Parse("""
            // The shop.
            module "github.com/acme/shop/v3"

            go 1.25.1

            toolchain go1.26.3

            require (
            	github.com/gin-gonic/gin v1.10.0
            	golang.org/x/net v0.30.0 // indirect
            )

            require github.com/mattn/go-sqlite3 v1.14.22

            replace (
            	example.com/shared => ./shared
            	example.com/old v1.0.0 => example.com/new v1.1.0
            )
            """);

        Assert.Equal("github.com/acme/shop/v3", mod.Module);
        Assert.Equal("shop", mod.Name);
        Assert.Equal("1.25.1", mod.GoLine);
        Assert.Equal("go1.26.3", mod.ToolchainLine);
        Assert.Equal([new GoRequirement("github.com/gin-gonic/gin", "v1.10.0", false), new GoRequirement("golang.org/x/net", "v0.30.0", true), new GoRequirement("github.com/mattn/go-sqlite3", "v1.14.22", false)], mod.Requirements);
        Assert.Equal(["./shared"], mod.LocalReplacements);
        Assert.True(mod.Requires("github.com/gin-gonic/gin"));
        Assert.False(mod.Requires("github.com/gin-gonic"));
        Assert.Equal(new GoVersion(1, 26), GoImages.Resolve(mod).Version);
    }
}
