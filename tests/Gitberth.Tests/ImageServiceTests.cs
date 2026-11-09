using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Data;
using Gitberth.Docker;
using Gitberth.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Images on the host and in registries: pulls and their progress (from the stream Docker sends), what a registry says
/// about a reference (answers captured from Docker 29.9.2 on the host), the local list, the disk guard and removals.
/// </summary>
public class ImageServiceTests : IDisposable
{
    private const string Postgres18 = "sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42";

    private readonly ImageKit.Db _db = new();
    private readonly ImageKit.Engine _engine = new();

    public void Dispose() => _db.Dispose();

    private static List<JsonObject> Lines(string file) =>
        ImageKit.Fixture("Images", file).Split('\n').Select(DockerApi.ParsePullLine).OfType<JsonObject>().ToList();

    [Fact]
    public void Sums_a_pulls_layers_into_one_progress()
    {
        var lines = Lines("pull-postgres-18.jsonl");
        var tracker = new PullTracker();

        foreach (var line in lines.Take(2)) Assert.Null(tracker.Feed(line));
        // A layer already on the host is done at once; its size isn't counted.
        Assert.Equal(new PullProgress("Already exists", 0, 0, 1, 1), tracker.Progress);

        foreach (var line in lines.Skip(2).Take(4)) tracker.Feed(line);
        Assert.Equal(new PullProgress("Downloading", 1048576, 4194304, 3, 1), tracker.Progress);
        Assert.Equal(0.25, tracker.Progress.Fraction);

        foreach (var line in lines.Skip(6)) Assert.Null(tracker.Feed(line));
        Assert.Equal(new PullProgress("Downloaded newer image for postgres:18", 12582912, 12582912, 3, 3), tracker.Progress);
        Assert.Equal(Postgres18, tracker.Digest);
        Assert.False(tracker.UpToDate);
    }

    [Fact]
    public void Reads_the_containerd_stores_stream_as_docker_29_sends_it()
    {
        // Captured from the host pulling alpine:3.21: the index and config blobs come as "Download complete" alone, the
        // extraction counts seconds, and a layer this small has no byte counts.
        var lines = Lines("pull-alpine-3.21-containerd.jsonl");
        var tracker = new PullTracker();

        foreach (var line in lines.Take(6)) Assert.Null(tracker.Feed(line));
        Assert.Equal(new PullProgress("Extracting", 0, 0, 1, 0), tracker.Progress);
        Assert.Equal(0, tracker.Progress.Fraction);

        foreach (var line in lines.Skip(6)) Assert.Null(tracker.Feed(line));
        Assert.Equal(new PullProgress("Downloaded newer image for alpine:3.21", 0, 0, 1, 1), tracker.Progress);
        Assert.Equal(1, tracker.Progress.Fraction);
        Assert.Equal("sha256:87a4361f6c819618a6bafa2ed5228714cb0bb22195d2b49c95eb26799990128a", tracker.Digest);
    }

    [Fact]
    public async Task A_pull_reports_its_progress_and_the_image_it_got()
    {
        var images = ImageKit.Images(_engine, _db);
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-postgres-18.jsonl").Split('\n'),
            () => _engine.Add(ImageKit.Image(ImageKit.Id('b'), ["postgres:18"], ["postgres@" + Postgres18])));
        var seen = new List<PullProgress>();

        var job = images.StartPull("postgres:18");
        job.Changed += () => { lock (seen) seen.Add(job.Progress); };
        var result = await job.Completion;

        Assert.True(result.Ok, result.Message);
        Assert.Equal((ImageKit.Id('b'), Postgres18), (result.ImageId, result.Digest));
        Assert.Equal("Pulled postgres:18 (bbbbbbbbbbbb).", result.Message);
        Assert.Equal((3, 3), (job.Progress.Layers, job.Progress.LayersDone));
        Assert.Same(job, images.PullFor("postgres:18"));
        Assert.Same(job, images.PullFor("docker.io/library/postgres:18"));
        Assert.Equal(["pull docker.io/library/postgres:18"], _engine.Calls);
    }

    [Fact]
    public async Task An_error_line_fails_the_pull_with_dockers_words()
    {
        var images = ImageKit.Images(_engine, _db);
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-error.jsonl").Split('\n'), null);

        var result = await images.StartPull("postgres:18").Completion;

        Assert.False(result.Ok);
        Assert.Null(result.ImageId);
        Assert.Equal("Docker's disk is full: free some space, then try again. Docker said: failed to register layer: write /var/lib/postgresql/18/docker/base: no space left on device",
            result.Message);
    }

    [Fact]
    public async Task A_pull_of_what_the_host_has_is_up_to_date()
    {
        _engine.Add(ImageKit.Image("sha256:3a4264520e121176619f54ff867d12774bc81e95e7b84db58d63bccdb6144b81", ["postgres:18"]));
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-up-to-date.jsonl").Split('\n'), null);

        var result = await ImageKit.Images(_engine, _db).StartPull("postgres:18").Completion;

        Assert.True(result.Ok);
        Assert.Equal("postgres:18 is up to date (3a4264520e12).", result.Message);
    }

    [Fact]
    public async Task A_pull_is_refused_before_it_starts_when_dockers_disk_is_too_full()
    {
        var images = ImageKit.Images(_engine, _db, disk: new DiskSpace(3_330_000_000, 246L << 30));
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-postgres-18.jsonl").Split('\n'), null);

        var result = await images.StartPull("postgres:18").Completion;

        Assert.False(result.Ok);
        Assert.Equal("Only 3.1 GB free on Docker's disk. Free some space first.", result.Message);
        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task A_pull_running_for_a_reference_is_the_one_returned()
    {
        var images = ImageKit.Images(_engine, _db);
        _engine.PullGate = new TaskCompletionSource();
        _engine.Pulls["docker.io/library/postgres:18"] = (ImageKit.Fixture("Images", "pull-up-to-date.jsonl").Split('\n'),
            () => _engine.Add(ImageKit.Image(ImageKit.Id('c'), ["postgres:18"])));

        var first = images.StartPull("postgres:18");
        var second = images.StartPull("docker.io/library/postgres:18");
        Assert.Same(first, second);
        _engine.PullGate.SetResult();
        Assert.True((await first.Completion).Ok);

        // Once it's over, a new pull is a new job.
        Assert.NotSame(first, images.StartPull("postgres:18"));
    }

    [Fact]
    public async Task A_reference_docker_wont_take_is_never_pulled()
    {
        var result = await ImageKit.Images(_engine, _db).StartPull("Not An Image").Completion;

        Assert.False(result.Ok);
        Assert.StartsWith("Not An Image isn't an image name Docker takes", result.Message);
        Assert.Empty(_engine.Calls);
    }

    // ---- Through the Engine API client, against answers the real engine gave ----

    private sealed class Answers(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Call, string? Auth)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/version") return Task.FromResult(Json(DockerFixtures.Text("version.json")));
            var auth = request.Headers.TryGetValues(DockerApi.RegistryAuthHeader, out var values) ? values.Single() : null;
            lock (Calls) Calls.Add(($"{request.Method} {Uri.UnescapeDataString(request.RequestUri.PathAndQuery)}", auth));
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private ImageService Over(DockerApi api) =>
        new(api, new RegistryService(_db, ImageKit.Protector(), api), new GitberthOptions { DataDir = Path.GetTempPath() }, new ImageKit.NoHttp(),
            NullLogger<ImageService>.Instance) { ReadDisk = () => ImageKit.Plenty };

    [Fact]
    public async Task Streams_a_pull_line_by_line_with_the_registrys_credentials()
    {
        var answers = new Answers(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/v1.56/images/create"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ImageKit.Fixture("Images", "pull-postgres-18.jsonl")) }
            : Json("""{"message":"page not found"}""", HttpStatusCode.NotFound));
        using var api = new DockerApi("unix:///var/run/docker.sock", answers);

        var lines = new List<JsonObject>();
        await foreach (var line in api.PullAsync("docker.io/library/postgres", "18", "linux/amd64", "eyJ1c2VybmFtZSI6Im1lIn0="))
            lines.Add(line);

        Assert.Equal(18, lines.Count);
        var (call, auth) = Assert.Single(answers.Calls);
        Assert.Equal("POST /v1.56/images/create?fromImage=docker.io/library/postgres&tag=18&platform=linux/amd64", call);
        Assert.Equal("eyJ1c2VybmFtZSI6Im1lIn0=", auth);
    }

    [Fact]
    public async Task A_pull_docker_refuses_at_once_is_its_error()
    {
        var answers = new Answers(_ => Json("""{"message":"pull access denied for gbtestuc/none, repository does not exist or may require 'docker login'"}""", HttpStatusCode.NotFound));
        using var api = new DockerApi("unix:///var/run/docker.sock", answers);

        var refused = await Assert.ThrowsAsync<DockerApiException>(async () =>
        {
            await foreach (var _ in api.PullAsync("docker.io/gbtestuc/none", "1")) { }
        });
        Assert.Equal(404, refused.Status);

        var result = await Over(api).StartPull("gbtestuc/none:1").Completion;
        Assert.False(result.Ok);
        Assert.StartsWith("Docker Hub refused gbtestuc/none:1: it doesn't exist, or it's private and needs a sign-in (Settings › Registries).", result.Message);
    }

    [Fact]
    public async Task Reads_what_the_registry_says_found_refused_or_missing()
    {
        var answers = new Answers(r => Uri.UnescapeDataString(r.RequestUri!.AbsolutePath) switch
        {
            "/v1.56/distribution/docker.io/library/postgres:18/json" => Json(ImageKit.Fixture("Images", "distribution-postgres-18.json")),
            "/v1.56/distribution/docker.io/library/postgres:no-such-tag-gbtest/json" => Json(ImageKit.Fixture("Images", "distribution-no-tag.json"), HttpStatusCode.NotFound),
            "/v1.56/distribution/docker.io/gbtestuc/no-such-repo:1/json" => Json(ImageKit.Fixture("Images", "distribution-no-repo.json"), HttpStatusCode.Forbidden),
            var path => Json($$"""{"message":"page not found: {{path}}"}""", HttpStatusCode.NotFound),
        });
        using var api = new DockerApi("unix:///var/run/docker.sock", answers);
        var images = Over(api);

        var found = await images.ResolveAsync("postgres:18");
        Assert.True(found.Found);
        Assert.Equal(Postgres18, found.Digest);
        Assert.Equal(["linux/amd64", "linux/arm/v5", "linux/arm/v7", "linux/arm64/v8", "linux/386", "linux/ppc64le", "linux/riscv64", "linux/s390x"], found.Platforms);
        Assert.Null(found.Error);

        var missing = await images.ResolveAsync("postgres:no-such-tag-gbtest");
        Assert.Equal((false, false), (missing.Found, missing.Unauthorized));
        Assert.Equal("Docker Hub has no postgres:no-such-tag-gbtest. Docker said: manifest unknown: manifest unknown", missing.Error);

        var refused = await images.ResolveAsync("gbtestuc/no-such-repo:1");
        Assert.Equal((false, true), (refused.Found, refused.Unauthorized));
        Assert.StartsWith("Docker Hub refused gbtestuc/no-such-repo:1: it doesn't exist, or it's private and needs a sign-in (Settings › Registries).", refused.Error);

        var notOne = await images.ResolveAsync("Not An Image");
        Assert.False(notOne.Found);
        Assert.StartsWith("Not An Image isn't an image name Docker takes", notOne.Error);
        // Signed out: no credentials go with the call.
        Assert.All(answers.Calls, c => Assert.Null(c.Auth));
    }

    [Fact]
    public async Task Sends_the_registrys_credentials_when_there_are_some()
    {
        var answers = new Answers(_ => Json(ImageKit.Fixture("Images", "distribution-postgres-18.json")));
        using var api = new DockerApi("unix:///var/run/docker.sock", answers);
        await using (var db = _db.CreateDbContext())
        {
            db.Registries.Add(new Data.RegistryCredential { Host = "ghcr.io", Username = "me", ProtectedSecret = "not readable" });
            await db.SaveChangesAsync();
        }
        var protector = ImageKit.Protector();
        var registries = new RegistryService(_db, protector, api);
        await using (var db = _db.CreateDbContext())
        {
            db.Registries.Single().ProtectedSecret = protector.Protect("ghp_token");
            await db.SaveChangesAsync();
        }
        var images = new ImageService(api, registries, new GitberthOptions { DataDir = Path.GetTempPath() }, new ImageKit.NoHttp(), NullLogger<ImageService>.Instance);

        await images.ResolveAsync("ghcr.io/me/app:1.2");
        await images.ResolveAsync("postgres:18");

        Assert.NotNull(answers.Calls[0].Auth);
        Assert.Equal("ghp_token", (string)JsonNode.Parse(Convert.FromBase64String(answers.Calls[0].Auth!.Replace('-', '+').Replace('_', '/')))!["password"]!);
        Assert.Null(answers.Calls[1].Auth);
    }

    [Theory]
    [InlineData("Get \"https://registry.lan:5000/v2/\": http: server gave HTTP response to HTTPS client", 500, "registry.lan:5000 answers over plain HTTP")]
    [InlineData("Get \"https://registry.lan:5000/v2/\": tls: failed to verify certificate: x509: certificate signed by unknown authority", 500, "Docker doesn't trust registry.lan:5000's certificate")]
    [InlineData("toomanyrequests: You have reached your pull rate limit.", 500, "registry.lan:5000's limit on pulls is reached for now")]
    [InlineData("Get \"https://registry.lan:5000/v2/\": dial tcp: lookup registry.lan: no such host", 500, "Docker couldn't reach registry.lan:5000")]
    [InlineData("something else", 500, "something else")]
    public void Explains_dockers_registry_errors_without_changing_dockers_settings(string message, int status, string says)
    {
        var text = ImageService.Explain(message, ImageReference.Parse("registry.lan:5000/a/b:1")!, status);

        Assert.StartsWith(says, text);
        Assert.EndsWith(message, text);
    }

    [Fact]
    public void Lists_named_images_newest_first_with_their_containers()
    {
        var images = JsonNode.Parse("""
            [
              {"Id":"sha256:aaa","RepoTags":["postgres:18","postgres:latest"],"RepoDigests":["postgres@sha256:1"],"Created":1761615024,"Size":456000000,"Containers":-1},
              {"Id":"sha256:bbb","RepoTags":["<none>:<none>"],"RepoDigests":[],"Created":1762000000,"Size":1000,"Containers":-1},
              {"Id":"sha256:ccc","RepoTags":[],"RepoDigests":["x@sha256:2"],"Created":1762000001,"Size":1000,"Containers":-1},
              {"Id":"sha256:ddd","RepoTags":["gitberth/kanban:0d1f2e3"],"RepoDigests":[],"Created":1762100000,"Size":90000000,"Containers":-1},
              {"Id":"sha256:eee","RepoTags":["gitberth:local"],"RepoDigests":[],"Created":1762200000,"Size":300000000,"Containers":-1},
              {"Id":"sha256:fff","RepoTags":["gitberth-sample-node-express:latest"],"RepoDigests":[],"Created":1762150000,"Size":80000000,"Containers":-1}
            ]
            """)!.AsArray();
        var containers = JsonNode.Parse("""[{"ImageID":"sha256:aaa"},{"ImageID":"sha256:aaa"},{"ImageID":"sha256:fff"},{"ImageID":"sha256:bbb"}]""")!.AsArray();

        var list = ImageService.ParseLocal(images, containers);

        Assert.Equal(["sha256:eee", "sha256:fff", "sha256:ddd", "sha256:aaa"], list.Select(i => i.Id));
        var postgres = list.Single(i => i.Id == "sha256:aaa");
        Assert.Equal(["postgres:18", "postgres:latest"], postgres.Tags);
        Assert.Equal((2, false, 456000000L), (postgres.Containers, postgres.BuiltByGitberth, postgres.SizeBytes));
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1761615024), postgres.Created);
        Assert.True(list.Single(i => i.Id == "sha256:ddd").BuiltByGitberth);
        Assert.True(list.Single(i => i.Id == "sha256:eee").BuiltByGitberth);
        Assert.False(list.Single(i => i.Id == "sha256:fff").BuiltByGitberth);
        Assert.Equal(1, list.Single(i => i.Id == "sha256:fff").Containers);
    }

    [Theory]
    [InlineData(3_330_000_000L, 264_000_000_000L, true, true, "Only 3.1 GB free on Docker's disk. Free some space first.")]
    [InlineData(12_000_000_000L, 200_000_000_000L, false, true, "Only 11.2 GB free on Docker's disk.")]
    [InlineData(12_000_000_000L, 264_000_000_000L, true, true, "Only 11.2 GB free on Docker's disk. Free some space first.")]
    [InlineData(20_000_000_000L, 1_000_000_000_000L, true, false, "18.6 GB free on Docker's disk. Free some space first.")]
    [InlineData(172_000_000_000L, 264_000_000_000L, false, false, null)]
    public void Guards_dockers_disk(long free, long total, bool tooLow, bool low, string? problem)
    {
        var disk = new DiskSpace(free, total);

        Assert.Equal((tooLow, low, problem), (disk.TooLow, disk.Low, disk.Problem));
    }

    [Fact]
    public async Task Searches_docker_hub_official_images_first()
    {
        var results = await ImageKit.Images(_engine, _db).SearchAsync("postgres");

        Assert.Equal(["postgres", "circleci/postgres", "cimg/postgres", "kasmweb/postgres", "elestio/postgres"], results.Select(r => r.Name));
        Assert.Equal(new HubResult("postgres", "The PostgreSQL object-relational database system provides reliability and data integrity.", 15048, true), results[0]);
        Assert.Empty(await ImageKit.Images(_engine, _db).SearchAsync("  "));
    }

    [Fact]
    public async Task Reads_docker_hubs_tags_and_nothing_elsewhere()
    {
        var hub = new Hub("""{"count":2,"results":[{"name":"18","last_updated":"2026-11-01"},{"name":"17"}]}""");
        var images = ImageKit.Images(_engine, _db, http: hub);

        Assert.Equal(["18", "17"], await images.TagsAsync(ImageReference.Parse("postgres")!));
        Assert.Equal("https://hub.docker.com/v2/namespaces/library/repositories/postgres/tags?page_size=100&ordering=last_updated", Assert.Single(hub.Asked));
        Assert.Empty(await images.TagsAsync(ImageReference.Parse("ghcr.io/o/r")!));
        Assert.Empty(await ImageKit.Images(_engine, _db).TagsAsync(ImageReference.Parse("postgres")!));
    }

    private sealed class Hub(string answer) : IHttpClientFactory
    {
        public List<string> Asked { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(ImageService.HubClient, name);
            return new HttpClient(new Handler(this, answer));
        }

        private sealed class Handler(Hub hub, string answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                hub.Asked.Add(request.RequestUri!.ToString());
                return Task.FromResult(Json(answer));
            }
        }
    }

    [Fact]
    public async Task Reads_an_images_facts()
    {
        _engine.Add(DockerFixtures.Json("image-postgres.json").AsObject());

        var facts = await ImageKit.Images(_engine, _db).FactsAsync("postgres:18");

        Assert.NotNull(facts);
        Assert.Equal([new ExposedPort(5432, "tcp")], facts.ExposedPorts);
        Assert.Equal(["/var/lib/postgresql"], facts.Volumes);
        // A registry's once one is known to have it (a check or a pull); its RepoDigests alone don't say.
        Assert.Equal(Apps.ImageOrigin.Local, facts.Origin);
        await using (var db = _db.CreateDbContext())
        {
            db.ImageChecks.Add(new ImageCheck { Reference = "docker.io/library/postgres:18", RemoteDigest = "sha256:" + new string('e', 64), CheckedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(Apps.ImageOrigin.Registry, (await ImageKit.Images(_engine, _db).FactsAsync("postgres:18"))!.Origin);
        Assert.Null(await ImageKit.Images(_engine, _db).FactsAsync("redis:7"));
    }

    // DATA-5 and L2 (changed by the backend fixer): only an untagged image goes, never one with a name someone gave it, nor
    // one an app keeps for Roll back.
    [Fact]
    public async Task Removes_an_image_only_when_nothing_uses_it_it_has_no_name_and_no_app_keeps_it()
    {
        var images = ImageKit.Images(_engine, _db);
        _engine.Add(ImageKit.Image(ImageKit.Id('1'), []));
        _engine.Add(ImageKit.Image(ImageKit.Id('2'), ["old:1"]));
        _engine.Add(ImageKit.Image(ImageKit.Id('3'), []));
        _engine.Add(ImageKit.Image(ImageKit.Id('4'), ["a:1", "b:1"]));
        _engine.Add(ImageKit.Image(ImageKit.Id('5'), []));
        _engine.Uses("pg", ImageKit.Id('3'));
        await _db.AddAsync(new AppDefinition { Name = "web", Slug = "web", Source = Apps.AppSource.Registry, PreviousImage = ImageKit.Id('5') });

        Assert.Null(await images.RemoveIfUnusedAsync(ImageKit.Id('1')));
        Assert.Equal("222222222222 has a name of its own (old:1), so Gitberth left it.", await images.RemoveIfUnusedAsync(ImageKit.Id('2')));
        Assert.Null(await images.RemoveIfUnusedAsync(ImageKit.Id('3')));
        Assert.Equal("444444444444 has a name of its own (a:1, b:1), so Gitberth left it.", await images.RemoveIfUnusedAsync(ImageKit.Id('4')));
        Assert.Equal("555555555555 is kept for an app's Roll back, so Gitberth left it.", await images.RemoveIfUnusedAsync(ImageKit.Id('5')));
        Assert.Null(await images.RemoveIfUnusedAsync(ImageKit.Id('9')));

        Assert.Equal([$"remove {ImageKit.Id('1')}"], _engine.Calls);
        Assert.Equal([ImageKit.Id('2'), ImageKit.Id('3'), ImageKit.Id('4'), ImageKit.Id('5')], _engine.Images.Select(i => (string)i["Id"]!));
    }
}
