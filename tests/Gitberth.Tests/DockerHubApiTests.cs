using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Gitberth.Images;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gitberth.Tests;

/// <summary>
/// Docker Hub's search and tags as Gitberth asks for them (DockerHubApi): the answers trimmed from the probes of
/// 2026-11-22 (research R2, R4–R8), and every way Docker Hub can fail to answer. No network: a fake answers, and a clock the
/// test moves stands in for waiting.
/// </summary>
public sealed class DockerHubApiTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private const string V4 = "https://hub.docker.com/api/search/v4?query=nginx&from=0&size=25&type=image";
    private const string V1 = "https://index.docker.io/v1/search?q=nginx&n=25";
    private const string NginxPage1 = "https://hub.docker.com/v2/namespaces/library/repositories/nginx/tags?page=1&page_size=100&ordering=last_updated";

    private readonly ImageKit.Clock _clock = new(Start);
    private readonly FakeHttp _http = new();
    private readonly DockerHubApi _hub;

    public DockerHubApiTests() => _hub = new DockerHubApi(_http, _clock, new HostGates(_clock), NullLogger.Instance);

    private static string Fixture(string file) => ImageKit.Fixture("DockerHub", file);

    private static readonly ImageReference Nginx = ImageReference.Parse("nginx")!;

    private Task<HubSearch> SearchAsync(string query, string architecture = "amd64") => _hub.SearchAsync(query, architecture, CancellationToken.None);

    private Task<HubTags> TagsAsync(ImageReference? reference = null, int pages = 3, string? name = null, HostPlatform? host = null) =>
        _hub.TagsAsync(reference ?? Nginx, pages, name, host ?? HostPlatform.Default, CancellationToken.None);

    // A page of n tags of a repository with count tags, as Docker Hub's tags API answers it.
    private static string Page(int count, int page)
    {
        var size = Math.Clamp(count - (page - 1) * 100, 0, 100);
        return new JsonObject
        {
            ["count"] = count,
            ["results"] = new JsonArray(Enumerable.Range(0, size).Select(i => (JsonNode)new JsonObject
            {
                ["name"] = $"1.{page}.{i}",
                ["digest"] = "sha256:" + $"{page:x2}{i:x2}".PadRight(64, '0'),
                ["content_type"] = "image",
                ["tag_last_pushed"] = "2026-11-20T00:00:00Z",
                ["full_size"] = 1000,
                ["images"] = new JsonArray(new JsonObject { ["os"] = "linux", ["architecture"] = "amd64", ["size"] = 1000 }),
            }).ToArray()),
        }.ToJsonString();
    }

    private static int PageOf(string url) => int.Parse(url.Split("page=")[1].Split('&')[0], CultureInfo.InvariantCulture);

    private static HttpResponseMessage TooMany(Action<HttpResponseHeaders>? headers = null)
    {
        var response = FakeHttp.Json("""{"message":"too many requests"}""", HttpStatusCode.TooManyRequests);
        headers?.Invoke(response.Headers);
        return response;
    }

    // ---- search ----

    [Fact]
    public async Task The_full_search_gives_badges_pulls_dates_and_architectures_and_leaves_out_archived_hardened_and_bad_names()
    {
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));

        var found = await SearchAsync("nginx");

        Assert.Equal([V4], _http.Urls);
        Assert.Equal(("nginx", 296665, false, LookupProblem.None, (string?)null, (DateTime?)null), (found.Query, found.Total, found.Basic, found.Problem, found.Message, found.RetryAt));
        Assert.Equal(["nginx", "linuxserver/nginx", "dtagdevsec/nginx", "bitnami/nginx", "arm64v8/nginx", "tiny/nginx-proxy"], found.Results.Select(r => r.Name));
        Assert.Equal([HubBadge.Official, HubBadge.SponsoredOss, HubBadge.SponsoredOss, HubBadge.VerifiedPublisher, HubBadge.None, HubBadge.None],
            found.Results.Select(r => r.Badge));
        var nginx = found.Results[0];
        Assert.Equal(("Official build of Nginx.", 21427, (long?)13_624_900_407, "1B+"), (nginx.Description, nginx.Stars, nginx.Pulls, nginx.PullsText));
        Assert.Equal(new DateTime(2026, 11, 20, 1, 52, 5, DateTimeKind.Utc).AddTicks(3_765_460), nginx.Updated);
        Assert.Equal(DateTimeKind.Utc, nginx.Updated!.Value.Kind);
        // Attestations' "unknown" isn't an architecture.
        Assert.Equal(["amd64", "arm64", "arm", "386"], nginx.Architectures);
        Assert.True(nginx.FitsHost);
        Assert.False(found.Results.Single(r => r.Name == "arm64v8/nginx").FitsHost);
        // Docker Hub's own words for a small count are the count; the description is plain text on one line.
        var tiny = found.Results[^1];
        Assert.Equal(("A small proxy works on any host", (long?)842, "842", true), (tiny.Description, tiny.Pulls, tiny.PullsText, tiny.FitsHost));
        Assert.Empty(tiny.Architectures);
        // A host none of them lists an image for: only the result that lists no architectures isn't marked.
        Assert.Equal(["tiny/nginx-proxy"], (await new DockerHubApi(_http, _clock, new HostGates(_clock), NullLogger.Instance).SearchAsync("nginx", "s390x", default))
            .Results.Where(r => r.FitsHost).Select(r => r.Name));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("unreadable")]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    [InlineData("not-a-search")]
    public async Task When_the_full_search_fails_the_basic_one_answers_with_official_images_marked(string how)
    {
        _http.Answer = r => r.RequestUri!.Host == DockerHubApi.IndexHost ? FakeHttp.Json(Fixture("search-v1-nginx.json")) : how switch
        {
            "500" => FakeHttp.Json("{}", HttpStatusCode.InternalServerError),
            "unreadable" => FakeHttp.Json("<html>Bad gateway</html>"),
            "not-a-search" => FakeHttp.Json("""{"message":"moved"}"""),
            "timeout" => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
            _ => throw new HttpRequestException("Name or service not known (hub.docker.com:443)"),
        };

        var found = await SearchAsync("nginx");

        Assert.Equal([V4, V1], _http.Urls);
        Assert.Equal((true, LookupProblem.None, (string?)null, 296652), (found.Basic, found.Problem, found.Message, found.Total));
        Assert.Equal(["nginx", "linuxserver/nginx", "bitnami/nginx"], found.Results.Select(r => r.Name));
        Assert.Equal([HubBadge.Official, HubBadge.None, HubBadge.None], found.Results.Select(r => r.Badge));
        var nginx = found.Results[0];
        Assert.Equal(((long?)13_624_900_407, (string?)null, (DateTime?)null, 0, true), (nginx.Pulls, nginx.PullsText, nginx.Updated, nginx.Architectures.Count, nginx.FitsHost));
    }

    [Fact]
    public async Task When_both_searches_fail_the_full_searchs_problem_is_told_without_a_body()
    {
        _http.Answer = r => r.RequestUri!.Host == DockerHubApi.IndexHost
            ? FakeHttp.Json("""{"detail":"backend s3cret-route down"}""", HttpStatusCode.BadGateway)
            : FakeHttp.Json("upstream said s3cret-route", HttpStatusCode.ServiceUnavailable);

        var found = await SearchAsync("nginx");

        Assert.Equal((LookupProblem.Failed, "Docker Hub's search didn't answer properly (HTTP 503). Type the image's full name meanwhile."), (found.Problem, found.Message));
        Assert.Empty(found.Results);
        // An answer that came but couldn't be read has no code to give.
        _http.Answer = _ => FakeHttp.Json("not json");
        Assert.Equal("Docker Hub's search didn't answer properly. Type the image's full name meanwhile.", (await SearchAsync("postgres")).Message);
    }

    [Fact]
    public async Task Docker_hub_that_cant_be_reached_is_said_so_and_left_alone_for_a_minute()
    {
        _http.Answer = _ => throw new HttpRequestException("Connection refused");

        var found = await SearchAsync("nginx");

        Assert.Equal(LookupProblem.Unreachable, found.Problem);
        Assert.Equal("Docker Hub can't be reached from this host, so there's no search. Type the image's full name, like nginx:1.27 or ghcr.io/owner/app:1.4.", found.Message);
        Assert.Equal([V4, V1], _http.Urls);
        _clock.Now = Start.AddSeconds(59);
        Assert.Equal(LookupProblem.Unreachable, (await SearchAsync("postgres")).Problem);
        Assert.Equal(LookupProblem.Unreachable, (await TagsAsync()).Problem);
        Assert.Equal(2, _http.Requests.Count);
        _clock.Now = Start.AddSeconds(61);
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));
        Assert.Equal(LookupProblem.None, (await SearchAsync("postgres")).Problem);
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task A_429_holds_docker_hub_until_retry_after_and_the_basic_search_isnt_asked_instead()
    {
        _http.Answer = _ => TooMany(h => h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        var found = await SearchAsync("nginx");

        Assert.Equal((LookupProblem.RateLimited, (DateTime?)Start.AddSeconds(30).UtcDateTime), (found.Problem, found.RetryAt));
        Assert.Equal("Docker Hub asked Gitberth to wait before searching again. Type the image's full name meanwhile.", found.Message);
        Assert.Equal([V4], _http.Urls);
        // Held: not asked again, for a search or for tags, until then.
        _clock.Now = Start.AddSeconds(29);
        var held = await SearchAsync("postgres");
        Assert.Equal((LookupProblem.RateLimited, (DateTime?)Start.AddSeconds(30).UtcDateTime), (held.Problem, held.RetryAt));
        var tags = await TagsAsync();
        Assert.Equal((LookupProblem.RateLimited, (DateTime?)Start.AddSeconds(30).UtcDateTime), (tags.Problem, tags.RetryAt));
        Assert.Single(_http.Requests);
        _clock.Now = Start.AddSeconds(31);
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));
        Assert.Equal(LookupProblem.None, (await SearchAsync("postgres")).Problem);
    }

    [Theory]
    // Never under 10 seconds, nor over 10 minutes.
    [InlineData("retry-after", 3, 10)]
    [InlineData("retry-after", 3600, 600)]
    [InlineData("retry-after-date", 90, 90)]
    // Without Retry-After, the reset Docker Hub gives (a Unix time); without either, a minute.
    [InlineData("reset", 45, 45)]
    [InlineData("none", 0, 60)]
    public async Task A_429_holds_as_long_as_it_says_between_ten_seconds_and_ten_minutes(string header, int value, int seconds)
    {
        _http.Answer = _ => TooMany(h =>
        {
            if (header == "retry-after") h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(value));
            if (header == "retry-after-date") h.RetryAfter = new RetryConditionHeaderValue(Start.AddSeconds(value));
            if (header == "reset") h.Add("x-ratelimit-reset", Start.AddSeconds(value).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        });

        Assert.Equal(Start.AddSeconds(seconds).UtcDateTime, (await SearchAsync("nginx")).RetryAt);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("0", true)]
    [InlineData("4", false)]
    [InlineData("176, 180;w=60", false)]
    public async Task An_answer_with_three_requests_left_or_fewer_holds_docker_hub_until_its_reset(string remaining, bool holds)
    {
        _http.Answer = _ =>
        {
            var response = FakeHttp.Json(Fixture("search-v4-nginx.json"));
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
            response.Headers.Add("x-ratelimit-reset", Start.AddSeconds(40).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return response;
        };

        Assert.Equal(LookupProblem.None, (await SearchAsync("nginx")).Problem);
        var next = await SearchAsync("postgres");

        Assert.Equal(holds ? LookupProblem.RateLimited : LookupProblem.None, next.Problem);
        Assert.Equal(holds ? Start.AddSeconds(40).UtcDateTime : null, next.RetryAt);
        Assert.Equal(holds ? 1 : 2, _http.Requests.Count);
    }

    [Fact]
    public async Task The_rigs_hub_wait_shortens_how_long_a_host_is_held_or_left_alone_and_nothing_else()
    {
        using var db = new ImageKit.Db();
        var images = ImageKit.Images(new ImageKit.Engine(), db, http: _http, time: _clock);
        TestTimings.Parse("hubWait=2")!.Apply(new ServiceCollection().AddSingleton(images).BuildServiceProvider());
        Assert.Equal(TimeSpan.FromSeconds(2), images.HostWaitCap);

        // Asked to wait 30 s: held 2 s, and the countdown says so.
        _http.Answer = _ => TooMany(h => h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));
        Assert.Equal(Start.AddSeconds(2).UtcDateTime, (await images.SearchAsync("nginx")).RetryAt);
        // Not reached: left alone 2 s, not a minute.
        _clock.Now = Start.AddSeconds(3);
        _http.Answer = _ => throw new HttpRequestException("Connection refused");
        Assert.Equal(LookupProblem.Unreachable, (await images.SearchAsync("postgres")).Problem);
        Assert.Equal(3, _http.Requests.Count);
        _clock.Now = Start.AddSeconds(6);
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));
        Assert.Equal(LookupProblem.None, (await images.SearchAsync("redis")).Problem);
        Assert.Equal(4, _http.Requests.Count);
        // As every test timing: only ever shorter.
        var installed = ImageKit.Images(new ImageKit.Engine(), db);
        Assert.Contains("isn't shorter than", Assert.Throws<InvalidOperationException>(() =>
            TestTimings.Parse("hubWait=60")!.Apply(new ServiceCollection().AddSingleton(installed).BuildServiceProvider())).Message, StringComparison.Ordinal);
        Assert.Null(installed.HostWaitCap);
    }

    [Fact]
    public async Task An_answer_is_kept_ten_minutes_whatever_the_case_of_the_query()
    {
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));

        await SearchAsync("nginx");
        var again = await SearchAsync("NGINX");

        Assert.Equal(("NGINX", 6), (again.Query, again.Results.Count));
        Assert.Single(_http.Requests);
        _clock.Now = Start.AddMinutes(10).AddSeconds(1);
        await SearchAsync("nginx");
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_basic_answer_is_kept_two_minutes_so_the_full_search_is_asked_again_soon()
    {
        _http.Answer = r => r.RequestUri!.Host == DockerHubApi.IndexHost ? FakeHttp.Json(Fixture("search-v1-nginx.json")) : FakeHttp.Json("{}", HttpStatusCode.BadGateway);
        Assert.True((await SearchAsync("nginx")).Basic);
        Assert.True((await SearchAsync("nginx")).Basic);
        Assert.Equal(2, _http.Requests.Count);

        _clock.Now = Start.AddMinutes(2).AddSeconds(1);
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));
        Assert.False((await SearchAsync("nginx")).Basic);
    }

    [Fact]
    public async Task A_cancelled_search_throws_and_leaves_nothing_behind()
    {
        _http.Answer = _ => FakeHttp.Json(Fixture("search-v4-nginx.json"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _hub.SearchAsync("nginx", "amd64", cancelled.Token));

        // Neither kept nor taken for Docker Hub being down.
        Assert.Equal(LookupProblem.None, (await SearchAsync("nginx")).Problem);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task A_gzipped_answer_is_read_and_one_that_inflates_past_its_cap_fails()
    {
        _http.Answer = r => r.RequestUri!.Host == DockerHubApi.IndexHost ? FakeHttp.Json("{}", HttpStatusCode.InternalServerError) : FakeHttp.Gzip(Fixture("search-v4-nginx.json"));
        Assert.Equal(6, (await SearchAsync("nginx")).Results.Count);

        // 3 MiB of spaces gzip to a few KB: the cap counts what it inflates to.
        _http.Answer = r => r.RequestUri!.Host == DockerHubApi.IndexHost ? FakeHttp.Json("{}", HttpStatusCode.InternalServerError)
            : FakeHttp.Gzip("{\"total\":1,\"results\":[],\"pad\":\"" + new string(' ', 3 << 20) + "\"}");
        Assert.Equal(LookupProblem.Failed, (await SearchAsync("postgres")).Problem);
    }

    [Fact]
    public async Task Every_request_is_gitberths_own_asks_for_json_and_never_signs_in()
    {
        _http.Answer = r => r.RequestUri!.AbsolutePath switch
        {
            "/api/search/v4" => FakeHttp.Json("{}", HttpStatusCode.InternalServerError),
            "/v1/search" => FakeHttp.Json(Fixture("search-v1-nginx.json")),
            _ => FakeHttp.Json(Page(150, PageOf(r.RequestUri.AbsoluteUri))),
        };

        await SearchAsync("nginx");
        await TagsAsync();

        Assert.Equal(4, _http.Requests.Count);
        Assert.All(_http.Requests, r => Assert.Equal((ImageService.HubClient, $"Gitberth/{AppVersion.Number}", "application/json", (string?)null),
            (r.Client, r.UserAgent, r.Accept, r.Authorization)));
    }

    // ---- tags ----

    [Theory]
    [InlineData(250, 3, new[] { 1, 2, 3 }, false)]
    [InlineData(130, 3, new[] { 1, 2 }, false)]
    [InlineData(90, 3, new[] { 1 }, false)]
    [InlineData(250, 1, new[] { 1 }, true)]
    [InlineData(1409, 3, new[] { 1, 2, 3 }, true)]
    [InlineData(1409, 12, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, true)]
    public async Task Tags_read_page_one_then_the_pages_its_count_needs_at_once(int count, int pages, int[] read, bool more)
    {
        _http.Answer = r => FakeHttp.Json(Page(count, PageOf(r.RequestUri!.AbsoluteUri)));

        var tags = await TagsAsync(pages: pages);

        Assert.Equal(NginxPage1, _http.Urls[0]);
        Assert.Equal(read, _http.Urls.Select(PageOf).Order());
        Assert.Equal((LookupProblem.None, count, more), (tags.Problem, tags.Total, tags.More));
        Assert.Equal(read.Sum(p => Math.Clamp(count - (p - 1) * 100, 0, 100)), tags.Tags.Count);
    }

    [Fact]
    public async Task Pages_read_are_kept_so_load_older_asks_only_for_the_next_one()
    {
        _http.Answer = r => FakeHttp.Json(Page(1409, PageOf(r.RequestUri!.AbsoluteUri)));

        await TagsAsync(pages: 3);
        var older = await TagsAsync(pages: 4);

        Assert.Equal([1, 2, 3, 4], _http.Urls.Select(PageOf));
        Assert.Equal(400, older.Tags.Count);
    }

    [Fact]
    public async Task A_later_page_that_fails_is_left_out_and_more_says_so()
    {
        _http.Answer = r => PageOf(r.RequestUri!.AbsoluteUri) == 2 ? FakeHttp.Json("{}", HttpStatusCode.BadGateway) : FakeHttp.Json(Page(250, PageOf(r.RequestUri!.AbsoluteUri)));

        var tags = await TagsAsync();

        Assert.Equal((LookupProblem.None, 150, true), (tags.Problem, tags.Tags.Count, tags.More));
    }

    [Fact]
    public async Task A_name_filter_reads_its_first_page_only()
    {
        _http.Answer = _ => FakeHttp.Json(Page(250, 1));

        var tags = await TagsAsync(ImageReference.Parse("louislam/uptime-kuma:2")!, 3, "2.0 beta");

        Assert.Equal(["https://hub.docker.com/v2/namespaces/louislam/repositories/uptime-kuma/tags?page=1&page_size=100&ordering=last_updated&name=2.0%20beta"], _http.Urls);
        Assert.True(tags.More);
    }

    [Fact]
    public async Task A_tag_page_gives_digests_push_dates_platforms_and_this_hosts_size_and_only_images()
    {
        _http.Answer = _ => FakeHttp.Json(Fixture("tags-nginx-1.json"));

        var tags = await TagsAsync(pages: 1, host: HostPlatform.Of("linux", "aarch64"));

        Assert.Equal(["stable-alpine3.24", "stable-alpine", "1.30.7-alpine3.24", "windowsservercore-ltsc2025",
            "sha256-caf4e1e032a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c.sig", "1.31.2-perl"], tags.Tags.Select(t => t.Name));
        var first = tags.Tags[0];
        Assert.Equal("sha256:caf4e1e032a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c", first.Digest);
        Assert.Equal(new DateTime(2026, 11, 20, 1, 52, 3, DateTimeKind.Utc).AddTicks(1_860_660), first.Pushed);
        // Attestations (unknown/unknown) aren't platforms; the size is this host's image's.
        Assert.Equal(["linux/amd64", "linux/arm64/v8", "linux/arm/v7"], first.Platforms!);
        Assert.Equal(25_110_231, first.Size);
        // last_updated when tag_last_pushed is missing.
        Assert.Equal(new DateTime(2026, 11, 20, 1, 52, 1, DateTimeKind.Utc), tags.Tags[2].Pushed);
        // Windows only: kept here (the ranking hides it), with no size for this host.
        Assert.Equal(["windows/amd64"], tags.Tags[3].Platforms!);
        Assert.Null(tags.Tags[3].Size);
        // A digest Docker wouldn't take isn't one.
        Assert.Equal(((string?)null, (long?)31_000_000), (tags.Tags[^1].Digest, tags.Tags[^1].Size));
        Assert.Equal(250, tags.Total);
    }

    [Fact]
    public async Task A_repository_docker_hub_doesnt_show_is_not_found_for_two_minutes()
    {
        _http.Answer = _ => FakeHttp.Json("""{"message":"object not found","errinfo":{}}""", HttpStatusCode.NotFound);
        var minio = ImageReference.Parse("minio/minio")!;

        var tags = await TagsAsync(minio);

        Assert.Equal((LookupProblem.NotFound, 404), (tags.Problem, tags.Status));
        Assert.Equal(["https://hub.docker.com/v2/namespaces/minio/repositories/minio/tags?page=1&page_size=100&ordering=last_updated"], _http.Urls);
        await TagsAsync(minio);
        Assert.Single(_http.Requests);
        _clock.Now = Start.AddMinutes(2).AddSeconds(1);
        await TagsAsync(minio);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_tag_page_over_its_cap_fails_whatever_its_length_says()
    {
        var big = "{\"count\":1,\"results\":[{\"name\":\"x\",\"pad\":\"" + new string('a', 5 << 20) + "\"}]}";
        _http.Answer = _ => FakeHttp.Json(big);

        var tags = await TagsAsync();

        Assert.Equal((LookupProblem.Failed, 200), (tags.Problem, tags.Status));
    }

    // ---- a private repository: Docker Hub's API doesn't show it, its registry lists it with the saved sign-in ----

    [Fact]
    public async Task A_private_docker_hub_repository_is_listed_by_its_registry_with_the_sign_in_sent_only_to_its_token_service()
    {
        using var db = new ImageKit.Db();
        var engine = new ImageKit.Engine();
        var protector = ImageKit.Protector();
        Assert.True((await new RegistryService(db, protector, engine).SaveAsync(new RegistryInput(null, "docker.io", "someone", "s3cret-pass"))).Ok);
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret-pass"));
        _http.Answer = r => (r.RequestUri!.Host, r.RequestUri.AbsolutePath) switch
        {
            ("hub.docker.com", _) => FakeHttp.Json("""{"message":"object not found","errinfo":{}}""", HttpStatusCode.NotFound),
            ("auth.docker.io", "/token") => r.Headers.Authorization?.ToString() == basic
                ? FakeHttp.Json("""{"token":"t0k3n","access_token":"t0k3n","expires_in":300}""") : FakeHttp.Json("{}", HttpStatusCode.Unauthorized),
            ("registry-1.docker.io", "/v2/someone/private/tags/list") when r.Headers.Authorization?.ToString() == "Bearer t0k3n" =>
                FakeHttp.Json("""{"name":"someone/private","tags":["1.0","1.0.0","1.1.0","latest"]}"""),
            ("registry-1.docker.io", _) => FakeHttp.Challenge("https://auth.docker.io/token", "registry.docker.io", "repository:someone/private:pull"),
            _ => FakeHttp.Json("{}", HttpStatusCode.NotFound),
        };
        var images = ImageKit.Images(engine, db, protector, http: _http, time: _clock);

        var list = await images.TagsAsync("someone/private:1.0");

        Assert.Equal((LookupProblem.None, "Docker Hub", "someone/private", false), (list.Problem, list.Registry, list.Image, list.Dates));
        Assert.Equal(["1.1.0", "1.0.0"], TagViews.For(list, "", false).Select(c => c.Tag));
        Assert.Equal([
            "https://hub.docker.com/v2/namespaces/someone/repositories/private/tags?page=1&page_size=100&ordering=last_updated -",
            "https://registry-1.docker.io/v2/someone/private/tags/list?n=1000 -",
            $"https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Asomeone%2Fprivate%3Apull {basic}",
            "https://registry-1.docker.io/v2/someone/private/tags/list?n=1000 Bearer t0k3n",
        ], _http.Calls);
        Assert.Equal([ImageService.HubClient, RegistryClient.ClientName, RegistryClient.ClientName, RegistryClient.ClientName], _http.Requests.Select(r => r.Client));
    }

    [Fact]
    public async Task Without_a_docker_hub_sign_in_a_repository_its_api_doesnt_show_is_not_found()
    {
        using var db = new ImageKit.Db();
        _http.Answer = _ => FakeHttp.Json("""{"message":"object not found","errinfo":{}}""", HttpStatusCode.NotFound);

        var list = await ImageKit.Images(new ImageKit.Engine(), db, http: _http, time: _clock).TagsAsync("nginxx");

        Assert.Equal(LookupProblem.NotFound, list.Problem);
        Assert.Equal("Docker Hub has no public image called nginxx: check the name, or add a Docker Hub sign-in in Settings › Registries if it's private.", list.Message);
        Assert.Single(_http.Requests);
        Assert.Empty(list.Versions);
    }

    [Fact]
    public async Task Hosts_named_by_hand_are_forgotten_once_they_need_nothing_kept()
    {
        var gates = new HostGates(_clock);
        for (var i = 0; i < HostGates.MaxHosts; i++) gates.Down($"r{i}.lan");
        using (await gates.SlotAsync("busy.lan", CancellationToken.None))
        {
            Assert.True(gates.Known > HostGates.MaxHosts);
            gates.Hold("held.lan", Start.AddMinutes(5));
            _clock.Now = Start.AddMinutes(2);
            gates.Down("late.lan");

            // The waits that are over are gone; what's still waited on, or under way, stays.
            Assert.Equal(3, gates.Known);
            Assert.True(gates.IsDown("late.lan"));
            Assert.Equal(Start.AddMinutes(5), gates.HeldUntil("held.lan"));
        }
    }

    [Fact]
    public async Task At_most_twelve_registry_listings_run_at_once_and_docker_hub_never_waits_for_them()
    {
        var gates = new HostGates(_clock);
        var taken = new List<IDisposable>();
        for (var i = 0; i < HostGates.Listings; i++) taken.Add(await gates.SlotAsync($"r{i}.lan", CancellationToken.None, listing: true));

        var next = gates.SlotAsync("r99.lan", CancellationToken.None, listing: true);
        // Docker Hub's own places are its own: a search goes out while every listing's place is taken.
        (await gates.SlotAsync(DockerHubApi.HubHost, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        await Task.Delay(100);
        Assert.False(next.IsCompleted);

        taken[0].Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        foreach (var slot in taken.Skip(1)) slot.Dispose();
    }

    [Fact]
    public async Task A_host_whose_places_are_waited_for_is_never_forgotten()
    {
        var gates = new HostGates(_clock);
        for (var i = 0; i < HostGates.MaxHosts; i++) gates.Down($"r{i}.lan");
        var busy = new List<IDisposable>();
        for (var i = 0; i < HostGates.PerHost; i++) busy.Add(await gates.SlotAsync("busy.lan", CancellationToken.None));
        var waiting = gates.SlotAsync("busy.lan", CancellationToken.None);

        // Everything else is forgotten; busy.lan's places stay, so a fifth request still waits for one of its four.
        _clock.Now = Start.AddMinutes(2);
        gates.Down("late.lan");
        Assert.Equal(2, gates.Known);
        var another = gates.SlotAsync("busy.lan", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted || another.IsCompleted);

        busy[0].Dispose();
        busy[1].Dispose();
        (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        (await another.WaitAsync(TimeSpan.FromSeconds(10))).Dispose();
        foreach (var slot in busy.Skip(2)) slot.Dispose();

        // Once nobody holds or waits for them, they're forgotten like the rest.
        for (var i = 0; i < HostGates.MaxHosts; i++) gates.Down($"s{i}.lan");
        _clock.Now = Start.AddMinutes(4);
        gates.Down("last.lan");
        Assert.Equal(1, gates.Known);
    }

    [Fact]
    public async Task A_search_that_waited_its_turn_behind_a_429_asks_nothing()
    {
        var gates = new HostGates(_clock);
        var hub = new DockerHubApi(_http, _clock, gates, NullLogger.Instance);
        var busy = new List<IDisposable>();
        for (var i = 0; i < HostGates.PerHost; i++) busy.Add(await gates.SlotAsync(DockerHubApi.HubHost, CancellationToken.None));

        var search = hub.SearchAsync("nginx", "amd64", CancellationToken.None);
        await Task.Delay(100);
        // Meanwhile one of the requests ahead of it heard Docker Hub ask Gitberth to wait.
        gates.Hold(DockerHubApi.HubHost, Start.AddSeconds(30));
        foreach (var slot in busy) slot.Dispose();
        var answer = await search.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal((LookupProblem.RateLimited, Start.AddSeconds(30).UtcDateTime), (answer.Problem, answer.RetryAt));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public void A_tag_page_is_read_no_further_than_a_hundred_tags_and_platforms_as_registries_write_them()
    {
        var results = Enumerable.Range(0, 150).Select(i => (JsonNode)new JsonObject
        {
            ["name"] = $"1.0.{i}",
            ["images"] = new JsonArray(
                new JsonObject { ["os"] = "linux", ["architecture"] = "amd64" },
                new JsonObject { ["os"] = "linux", ["architecture"] = "arm64‮" },
                new JsonObject { ["os"] = "linux", ["architecture"] = new string('x', 40) },
                new JsonObject { ["os"] = "linux", ["architecture"] = "arm", ["variant"] = "v7" }),
        }).ToArray();

        var page = DockerHubApi.ParseTags(new JsonObject { ["count"] = 150, ["results"] = new JsonArray(results) }, HostPlatform.Default)!;

        Assert.Equal(100, page.Tags.Count);
        Assert.All(page.Tags, t => Assert.Equal(["linux/amd64", "linux/arm/v7"], t.Platforms));
    }

    [Fact]
    public async Task An_answer_with_stacked_encodings_isnt_read()
    {
        var content = new StringContent("{}");
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentEncoding.Add("gzip");

        Assert.Null(await HostGates.ReadAsync(content, 1024, CancellationToken.None));
    }

    [Fact]
    public void Kept_answers_go_oldest_first_past_their_weight_in_all()
    {
        var kept = new TimedCache<string>(_clock, 10, s => s.Length, 10);
        kept.Set("a", "aaaa", TimeSpan.FromMinutes(10));
        kept.Set("b", "bbbb", TimeSpan.FromMinutes(10));
        kept.Set("c", "cccc", TimeSpan.FromMinutes(10));

        Assert.False(kept.TryGet("a", out _));
        Assert.True(kept.TryGet("b", out _) && kept.TryGet("c", out _));
        Assert.Equal(8, kept.Weight);

        // Set again, a key's weight is counted once.
        kept.Set("c", "cc", TimeSpan.FromMinutes(10));
        Assert.Equal(6, kept.Weight);
    }
}
