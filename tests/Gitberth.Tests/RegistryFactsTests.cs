using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Components.NewApp;
using Gitberth.Docker;
using Gitberth.Images;
using Gitberth.Network;
using Gitberth.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// New app from an image the host doesn't have yet: its config is read from its registry (the manifest, resolved to the
/// host's platform, then the config blob), so Configure shows its ports, volumes and check before it's pulled. And the
/// update policy Settings gives new apps, from any source. The registry is a stand-in: no network in tests.
/// </summary>
public sealed class RegistryFactsTests : IDisposable
{
    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();
    private readonly ImageKit.CreateSteps _steps = new();
    private readonly FakeRegistry _registry = new();

    public void Dispose() => _db.Dispose();

    private ImageService Images(Security.SecretProtector? protector = null) => ImageKit.Images(_engine, _db, protector, http: _registry);

    private AppCreator Creator(IHttpClientFactory? http = null) =>
        new(_steps, ImageKit.Images(_engine, _db, http: http ?? _registry), new GitberthOptions { DataDir = Path.GetTempPath(), Network = "gitberth" }, NullLogger<AppCreator>.Instance);

    [Fact]
    public async Task An_image_not_on_the_host_is_read_from_its_registry_for_this_hosts_platform()
    {
        var facts = await Images().RemoteFactsAsync("nginx:1.27");

        Assert.NotNull(facts);
        Assert.Equal((false, ImageOrigin.Registry), (facts.OnHost, facts.Origin));
        Assert.Equal([new ExposedPort(80, "tcp")], facts.ExposedPorts);
        Assert.Equal(["/usr/share/nginx/html"], facts.Volumes);
        Assert.Contains(facts.Env, e => e is { Key: "NGINX_VERSION", Value: "1.27.3", FromImage: true });
        Assert.Equal(["nginx", "-g", "daemon off;"], facts.Command);
        Assert.Equal(["/docker-entrypoint.sh"], facts.Entrypoint);
        Assert.Equal(HealthMode.Custom, facts.Health.Mode);
        Assert.Equal("SIGQUIT", facts.StopSignal);
        Assert.Equal(("linux", "amd64"), (facts.Os, facts.Architecture));
        // Anonymously, through Docker Hub's token service; the amd64 manifest of the index, never the arm64 one or an attestation.
        Assert.Equal(
        [
            "https://registry-1.docker.io/v2/library/nginx/manifests/1.27 -",
            "https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Alibrary%2Fnginx%3Apull -",
            "https://registry-1.docker.io/v2/library/nginx/manifests/1.27 Bearer t0k3n",
            $"https://registry-1.docker.io/v2/library/nginx/manifests/{FakeRegistry.Amd64} Bearer t0k3n",
            $"https://registry-1.docker.io/v2/library/nginx/blobs/{FakeRegistry.Config} Bearer t0k3n",
        ], _registry.Requests);
        // Nothing was pulled.
        Assert.DoesNotContain(_engine.Calls, c => c.StartsWith("pull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_arm_host_gets_the_arm_image()
    {
        _engine.Info["OSType"] = "linux";
        _engine.Info["Architecture"] = "aarch64";

        var facts = await Images().RemoteFactsAsync("nginx:1.27");

        Assert.Equal("arm64", facts!.Architecture);
        Assert.Contains(_registry.Requests, r => r.Contains($"manifests/{FakeRegistry.Arm64}", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_entry_is_picked_by_os_architecture_and_variant()
    {
        var index = new JsonArray(
            Entry("sha256:" + new string('1', 64), "linux", "arm", "v6"),
            Entry("sha256:" + new string('2', 64), "linux", "arm", "v7"),
            Entry("sha256:" + new string('3', 64), "linux", "arm64", "v8"),
            Entry("sha256:" + new string('4', 64), "unknown", "unknown", null));

        Assert.Equal("sha256:" + new string('2', 64), RegistryClient.Pick(index, HostPlatform.Of("linux", "armv7l")));
        Assert.Equal("sha256:" + new string('3', 64), RegistryClient.Pick(index, HostPlatform.Of("linux", "aarch64")));
        Assert.Null(RegistryClient.Pick(index, HostPlatform.Of("linux", "x86_64")));
        Assert.Equal(new HostPlatform("linux", "amd64", null), HostPlatform.Of(null, null));

        static JsonObject Entry(string digest, string os, string architecture, string? variant) => new()
        {
            ["digest"] = digest,
            ["platform"] = variant is null ? new JsonObject { ["os"] = os, ["architecture"] = architecture }
                : new JsonObject { ["os"] = os, ["architecture"] = architecture, ["variant"] = variant },
        };
    }

    [Fact]
    public async Task A_private_registrys_saved_sign_in_is_sent_to_its_token_service()
    {
        var protector = ImageKit.Protector();
        Assert.True((await new RegistryService(_db, protector, _engine).SaveAsync(new RegistryInput(null, "ghcr.io", "someone", "s3cret"))).Ok);

        var facts = await Images(protector).RemoteFactsAsync("ghcr.io/someone/web:1.2");

        Assert.NotNull(facts);
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret"));
        Assert.Contains($"https://ghcr.io/token?service=ghcr.io&scope=repository%3Asomeone%2Fweb%3Apull {basic}", _registry.Requests);
        // The sign-in goes only to the token service: the registry gets the token.
        Assert.DoesNotContain(_registry.Requests, r => r.StartsWith("https://ghcr.io/v2/", StringComparison.Ordinal) && r.EndsWith(basic, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_registry_that_cant_be_read_leaves_the_draft_as_before()
    {
        _registry.Down = true;

        var draft = await Creator().DraftFromImageAsync("nginx:1.27", AppSource.Registry);

        Assert.Null(draft.Facts);
        Assert.False(draft.ImageRead);
        Assert.Empty(draft.Settings.Ports);
        Assert.Equal("nginx:1.27 isn't on this host yet: once it's pulled, its ports and volumes are filled in.", Assert.Single(draft.Issues).Text);
    }

    [Fact]
    public async Task A_popular_image_not_yet_pulled_shows_its_ports_volumes_and_check_in_configure()
    {
        var creator = Creator();

        var draft = await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry);

        Assert.Equal([new PortRule(80, "tcp", PortAccess.Lan, 80)], draft.Settings.Ports);
        Assert.Equal(80, draft.Options.WebPort);
        Assert.Equal([new MountSetting(MountKind.Volume, "gb-nginx-html", "/usr/share/nginx/html")], draft.Settings.Mounts);
        Assert.Contains(draft.Settings.Env, e => e is { Key: "NGINX_VERSION", FromImage: true });
        Assert.True(draft.ImageRead);
        Assert.Empty(draft.Issues);

        // Configure and "What will run" show them, and still say it's pulled first.
        var form = NewAppForm.From(draft, draft.Facts);
        Assert.False(form.OnHost);
        Assert.Equal([80], form.Ports.Rules().Where(p => p.Published).Select(p => p.ContainerPort));
        Assert.Equal(["80/tcp"], form.Ports.ImageExposed);
        Assert.Equal(HealthMode.Custom, form.Facts!.Health.Mode);
        var plan = await creator.PlanAsync(form.ToDraft());
        Assert.True(plan.NeedsPull);
        Assert.StartsWith("Pulls nginx:1.27, then creates gb-nginx and starts it. Reached at ", plan.Consequence, StringComparison.Ordinal);
        Assert.EndsWith(":80.", plan.Consequence, StringComparison.Ordinal);
        // What the owner saw is what's created: the ports aren't filled in again after the pull.
        Assert.True(form.ToDraft().ImageRead);
    }

    [Fact]
    public async Task A_pasted_docker_run_of_an_image_not_yet_pulled_is_read_from_its_registry_too()
    {
        var draft = await Creator().DraftFromParsedAsync(DockerRunParser.Parse("docker run -d -P --name web nginx:1.27"));

        // -P's ports, the image's volume and its variables, before the pull.
        Assert.Equal([80], draft.Settings.Ports.Where(p => p.Published).Select(p => p.ContainerPort));
        Assert.Contains(draft.Settings.Mounts, m => m.Target == "/usr/share/nginx/html");
        Assert.Contains(draft.Settings.Env, e => e is { Key: "NGINX_VERSION", FromImage: true });
        Assert.True(draft.ImageRead);
        Assert.DoesNotContain(draft.Issues, i => i.Text.Contains("once it's pulled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ports_taken_out_in_configure_stay_out_after_the_pull()
    {
        _engine.Pulls["docker.io/library/nginx:1.27"] = (["{\"status\":\"Status: Downloaded newer image for nginx:1.27\"}"], () =>
            _engine.Add(ImageKit.Image(ImageKit.Id('6'), ["nginx:1.27"], ["nginx@" + ImageKit.Digest('6')], exposed: ["80/tcp"])));
        var creator = Creator();
        var draft = await creator.DraftFromImageAsync("nginx:1.27", AppSource.Registry);

        var plan = await creator.PlanAsync(NewAppForm.From(draft with { Settings = draft.Settings with { Ports = [] } }, draft.Facts).ToDraft());
        var result = await creator.Start(plan).Completion;

        Assert.True(result.Ok, result.Message);
        Assert.Empty(Assert.Single(_steps.Requests).Settings.Ports);
    }

    [Theory]
    [InlineData(UpdatePolicy.Off, "nginx:1.27", UpdatePolicy.Off)]
    [InlineData(UpdatePolicy.Auto, "nginx:1.27", UpdatePolicy.Auto)]
    [InlineData(UpdatePolicy.Notify, "nginx:1.27", UpdatePolicy.Notify)]
    // A database is never updated by itself: Automatically only tells for one.
    [InlineData(UpdatePolicy.Auto, "postgres:18", UpdatePolicy.Notify)]
    public async Task New_apps_get_the_update_policy_settings_give_them(UpdatePolicy setting, string image, UpdatePolicy expected)
    {
        _steps.DefaultPolicy = setting;
        _registry.Down = true;
        var creator = Creator();

        Assert.Equal(expected, (await creator.DraftFromImageAsync(image, AppSource.Registry)).Options.Updates);
        Assert.Equal(expected, (await creator.DraftFromImageAsync(image, AppSource.Manual)).Options.Updates);
        Assert.Equal(expected, (await creator.DraftFromParsedAsync(DockerRunParser.Parse($"docker run -d {image}"))).Options.Updates);
    }

    [Fact]
    public async Task The_setting_reaches_the_new_apps_row_but_a_digest_pin_follows_nothing()
    {
        _steps.DefaultPolicy = UpdatePolicy.Off;
        _engine.Add(ImageKit.Image(ImageKit.Id('6'), ["nginx:1.27"], ["nginx@" + ImageKit.Digest('6')], exposed: ["80/tcp"]));
        var creator = Creator();

        var form = NewAppForm.From(await creator.DraftFromImageAsync("nginx:1.27", AppSource.Local), null);
        await creator.Start(await creator.PlanAsync(form.ToDraft())).Completion;
        Assert.Equal(UpdatePolicy.Off, _steps.Rows[^1].Options.Updates);

        _steps.DefaultPolicy = UpdatePolicy.Auto;
        var pinned = await creator.DraftFromImageAsync("nginx@" + ImageKit.Digest('6'), AppSource.Local);
        await creator.Start(await creator.PlanAsync(pinned with { Name = "pinned" })).Completion;
        Assert.Equal(UpdatePolicy.Off, _steps.Rows[^1].Options.Updates);
    }

    [Fact]
    public async Task The_saved_setting_is_read_as_settings_writes_it()
    {
        var settings = new Services.SettingsService(_db, ImageKit.Protector());
        Assert.Equal(UpdatePolicy.Notify, await UpdateWatcher.DefaultPolicyAsync(settings));
        await settings.SetAsync(UpdateWatcher.DefaultPolicyKey, UpdatePolicy.Off.ToString());
        Assert.Equal(UpdatePolicy.Off, await UpdateWatcher.DefaultPolicyAsync(settings));
        await settings.SetAsync(UpdateWatcher.DefaultPolicyKey, "7");
        Assert.Equal(UpdatePolicy.Notify, await UpdateWatcher.DefaultPolicyAsync(settings));
    }

    /// <summary>
    /// Docker Hub and ghcr.io as far as reading an image goes: a 401 naming the token service until a token comes, a token
    /// for the asking (ghcr.io wants its sign-in), nginx:1.27 as an index (amd64, arm64 and an attestation), each
    /// platform's manifest, and the config. Every request is kept with its Authorization.
    /// </summary>
    private sealed class FakeRegistry : HttpMessageHandler, IHttpClientFactory
    {
        public static readonly string Amd64 = "sha256:" + new string('a', 64);
        public static readonly string Arm64 = "sha256:" + new string('b', 64);
        public static readonly string Config = "sha256:" + new string('c', 64);
        public static readonly string ArmConfig = "sha256:" + new string('e', 64);

        public List<string> Requests { get; } = [];

        /// <summary>Nothing answers: as if the registry couldn't be reached.</summary>
        public bool Down { get; set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down) throw new HttpRequestException("No route to the registry.");
            var url = request.RequestUri!.AbsoluteUri;
            var auth = request.Headers.Authorization?.ToString() ?? "-";
            lock (Requests) Requests.Add($"{url} {auth}");
            if (url.StartsWith("https://auth.docker.io/token?", StringComparison.Ordinal)) return Json("""{"token":"t0k3n"}""");
            if (url.StartsWith("https://ghcr.io/token?", StringComparison.Ordinal))
                return auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret")) ? Json("""{"token":"t0k3n"}""") : Answer(HttpStatusCode.Unauthorized);
            if (auth != "Bearer t0k3n")
            {
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
                unauthorized.Headers.WwwAuthenticate.ParseAdd(url.StartsWith("https://ghcr.io/", StringComparison.Ordinal)
                    ? """Bearer realm="https://ghcr.io/token",service="ghcr.io",scope="repository:someone/web:pull" """.Trim()
                    : """Bearer realm="https://auth.docker.io/token",service="registry.docker.io",scope="repository:library/nginx:pull" """.Trim());
                return Task.FromResult(unauthorized);
            }
            var path = request.RequestUri.AbsolutePath;
            return path switch
            {
                "/v2/library/nginx/manifests/1.27" => Json(Index(), "application/vnd.oci.image.index.v1+json"),
                "/v2/someone/web/manifests/1.2" => Json(Manifest(Config), "application/vnd.oci.image.manifest.v1+json"),
                _ when path.EndsWith("/manifests/" + Amd64, StringComparison.Ordinal) => Json(Manifest(Config), "application/vnd.oci.image.manifest.v1+json"),
                _ when path.EndsWith("/manifests/" + Arm64, StringComparison.Ordinal) => Json(Manifest(ArmConfig), "application/vnd.oci.image.manifest.v1+json"),
                _ when path.EndsWith("/blobs/" + Config, StringComparison.Ordinal) => Json(ConfigBlob("amd64")),
                _ when path.EndsWith("/blobs/" + ArmConfig, StringComparison.Ordinal) => Json(ConfigBlob("arm64")),
                _ => Answer(HttpStatusCode.NotFound),
            };
        }

        private static string Index() => new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.index.v1+json",
            ["manifests"] = new JsonArray(
                new JsonObject { ["digest"] = Amd64, ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" } },
                new JsonObject { ["digest"] = Arm64, ["platform"] = new JsonObject { ["os"] = "linux", ["architecture"] = "arm64", ["variant"] = "v8" } },
                new JsonObject { ["digest"] = "sha256:" + new string('d', 64), ["platform"] = new JsonObject { ["os"] = "unknown", ["architecture"] = "unknown" } }),
        }.ToJsonString();

        private static string Manifest(string config) => new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = new JsonObject { ["mediaType"] = "application/vnd.oci.image.config.v1+json", ["digest"] = config, ["size"] = 1234 },
            ["layers"] = new JsonArray(),
        }.ToJsonString();

        private static string ConfigBlob(string architecture) => new JsonObject
        {
            ["architecture"] = architecture,
            ["os"] = "linux",
            ["created"] = "2026-10-20T08:00:00Z",
            ["config"] = new JsonObject
            {
                ["ExposedPorts"] = new JsonObject { ["80/tcp"] = new JsonObject() },
                ["Volumes"] = new JsonObject { ["/usr/share/nginx/html"] = new JsonObject() },
                ["Env"] = new JsonArray("PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "NGINX_VERSION=1.27.3"),
                ["Entrypoint"] = new JsonArray("/docker-entrypoint.sh"),
                ["Cmd"] = new JsonArray("nginx", "-g", "daemon off;"),
                ["WorkingDir"] = "/",
                ["Labels"] = new JsonObject { ["maintainer"] = "NGINX Docker Maintainers" },
                ["StopSignal"] = "SIGQUIT",
                ["Healthcheck"] = new JsonObject { ["Test"] = new JsonArray("CMD-SHELL", "curl -fs http://localhost/ || exit 1"), ["Interval"] = 30_000_000_000L },
            },
        }.ToJsonString();

        private static Task<HttpResponseMessage> Json(string body, string type = "application/json") =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) });

        private static Task<HttpResponseMessage> Answer(HttpStatusCode status) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
    }
}
