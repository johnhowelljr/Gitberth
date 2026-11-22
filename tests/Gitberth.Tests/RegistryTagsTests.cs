using System.Net;
using System.Text;
using Gitberth.Images;

namespace Gitberth.Tests;

/// <summary>
/// A registry's own tag list (GET /v2/&lt;repo&gt;/tags/list) as the tag picker reads it, on the sign-in flow New app's
/// image read uses: anonymous first, a token from the service the 401 names, Basic only over HTTPS; pages by the Link
/// header on the same origin; the caps. Registries as the probes saw them (R13–R20), played by a fake.
/// </summary>
public sealed class RegistryTagsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private const string Api = "https://registry.e2e.test/v2/team/api/tags/list";

    private readonly ImageKit.Clock _clock = new(Start);
    private readonly FakeHttp _http = new();
    private readonly ImageKit.Db _db = new();

    public void Dispose() => _db.Dispose();

    private RegistryClient Client() => new(_http.CreateClient(RegistryClient.ClientName), _clock);

    private static ImageReference Ref(string text) => ImageReference.Parse(text)!;

    private Task<RegistryTags> ListAsync(string reference, RegistrySignIn? signIn = null) => Client().TagsAsync(Ref(reference), signIn, CancellationToken.None);

    private static string? Auth(HttpRequestMessage r) => r.Headers.Authorization?.ToString();

    private static HttpResponseMessage Tags(params string[] tags) =>
        FakeHttp.Json("{\"name\":\"team/api\",\"tags\":[" + string.Join(',', tags.Select(t => $"\"{t}\"")) + "]}");

    [Fact]
    public async Task A_public_registry_lists_its_tags_with_an_anonymous_token()
    {
        _http.Answer = r => (r.RequestUri!.AbsolutePath, Auth(r)) switch
        {
            ("/token", null) => FakeHttp.Json("""{"token":"anon-t0k3n","expires_in":300}"""),
            ("/v2/team/api/tags/list", "Bearer anon-t0k3n") => Tags("1.27.3", "latest", "sha-4f9c2e1", "not a tag", "-dash"),
            _ => FakeHttp.Challenge("https://registry.e2e.test/token", "registry.e2e.test", "repository:team/api:pull"),
        };

        var list = await ListAsync("registry.e2e.test/team/api:1.27");

        Assert.Equal(LookupProblem.None, list.Problem);
        // As listed, less what Docker wouldn't take as a tag; hiding is the ranking's.
        Assert.Equal(["1.27.3", "latest", "sha-4f9c2e1"], list.Tags);
        Assert.Equal([
            $"{Api}?n=1000 -",
            "https://registry.e2e.test/token?service=registry.e2e.test&scope=repository%3Ateam%2Fapi%3Apull -",
            $"{Api}?n=1000 Bearer anon-t0k3n",
        ], _http.Calls);
        Assert.All(_http.Requests, r => Assert.Equal($"Gitberth/{AppVersion.Number}", r.UserAgent));
    }

    [Fact]
    public async Task A_token_serves_one_listing_only()
    {
        _http.Answer = r => r.RequestUri!.AbsolutePath == "/token" ? FakeHttp.Json("""{"access_token":"anon-t0k3n"}""")
            : Auth(r) == "Bearer anon-t0k3n" ? Tags("1.0") : FakeHttp.Challenge("https://registry.e2e.test/token", "registry.e2e.test", "repository:team/api:pull");

        Assert.Equal(["1.0"], (await ListAsync("registry.e2e.test/team/api")).Tags);
        Assert.Equal(["1.0"], (await ListAsync("registry.e2e.test/team/api")).Tags);

        Assert.Equal(2, _http.Urls.Count(u => u.StartsWith("https://registry.e2e.test/token?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_sign_in_itself_goes_only_over_https()
    {
        var signIn = new RegistrySignIn("someone", "s3cret-pass");
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret-pass"));
        _http.Answer = r => Auth(r) == basic ? Tags("1.0") : FakeHttp.BasicChallenge();

        // A registry on this machine answers over plain HTTP: the sign-in isn't sent there.
        var plain = await ListAsync("localhost:5000/team/api", signIn);
        Assert.Equal((LookupProblem.NeedsSignIn, 401), (plain.Problem, plain.Status));
        Assert.All(_http.Requests, r => Assert.Null(r.Authorization));
        Assert.Equal(["http://localhost:5000/v2/team/api/tags/list?n=1000"], _http.Urls);

        var secure = await ListAsync("registry.lan/team/api", signIn);
        Assert.Equal(["1.0"], secure.Tags);
        Assert.Equal(["https://registry.lan/v2/team/api/tags/list?n=1000 -", $"https://registry.lan/v2/team/api/tags/list?n=1000 {basic}"], _http.Calls.Skip(1));
    }

    [Fact]
    public async Task A_relative_link_leads_to_the_next_page_on_the_same_registry()
    {
        _http.Answer = r => r.RequestUri!.Query.Contains("last=b", StringComparison.Ordinal) ? Tags("c", "d")
            : FakeHttp.Linked(Tags("a", "b"), "</v2/team/api/tags/list?last=b&n=1000>; rel=\"next\"");

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(["a", "b", "c", "d"], list.Tags);
        Assert.Equal([$"{Api}?n=1000", $"{Api}?last=b&n=1000"], _http.Urls);
    }

    [Theory]
    [InlineData("<https://elsewhere.test/v2/team/api/tags/list?last=b&n=1000>; rel=\"next\"")]
    [InlineData("<http://registry.e2e.test/v2/team/api/tags/list?last=b&n=1000>; rel=\"next\"")]
    [InlineData("<https://registry.e2e.test:8443/v2/team/api/tags/list?last=b&n=1000>; rel=\"next\"")]
    [InlineData("<//elsewhere.test/v2/team/api/tags/list?last=b&n=1000>; rel=\"next\"")]
    // Another link, not the next page's.
    [InlineData("</v2/team/api/tags/list?last=a&n=1000>; rel=\"prev\"")]
    public async Task A_link_to_another_origin_or_no_next_ends_the_list(string link)
    {
        _http.Answer = _ => FakeHttp.Linked(Tags("a", "b"), link);

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(LookupProblem.None, list.Problem);
        Assert.Equal(["a", "b"], list.Tags);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task At_most_ten_pages_are_read()
    {
        var pages = 0;
        _http.Answer = _ =>
        {
            var page = Interlocked.Increment(ref pages);
            return FakeHttp.Linked(Tags($"t{page}"), $"</v2/team/api/tags/list?last=t{page}&n=1000>; rel=\"next\"");
        };

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"t{i}"), list.Tags);
        Assert.Equal(10, _http.Requests.Count);
    }

    [Fact]
    public async Task At_most_ten_thousand_tags_are_read()
    {
        _http.Answer = _ => FakeHttp.Linked(Tags([.. Enumerable.Range(0, 10_050).Select(i => $"t{i}")]), "</v2/team/api/tags/list?last=t10049&n=1000>; rel=\"next\"");

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(10_000, list.Tags.Count);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task A_registry_that_refuses_a_thousand_at_once_is_asked_for_a_hundred()
    {
        _http.Answer = r => r.RequestUri!.Query.Contains("n=1000", StringComparison.Ordinal)
            ? FakeHttp.Json("""{"errors":[{"code":"PAGINATION_NUMBER_INVALID"}]}""", HttpStatusCode.BadRequest) : Tags("1.0");

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(["1.0"], list.Tags);
        Assert.Equal([$"{Api}?n=1000", $"{Api}?n=100"], _http.Urls);
    }

    [Theory]
    // The token service refuses (a private repository, asked anonymously), or the registry refuses even with a token.
    [InlineData("token-refused", LookupProblem.NeedsSignIn, 403)]
    [InlineData("still-401", LookupProblem.NeedsSignIn, 401)]
    [InlineData("no-challenge", LookupProblem.NeedsSignIn, 401)]
    [InlineData("404", LookupProblem.NotFound, 404)]
    [InlineData("500", LookupProblem.Failed, 500)]
    [InlineData("429", LookupProblem.RateLimited, 429)]
    [InlineData("unreadable", LookupProblem.Failed, 200)]
    [InlineData("unreachable", LookupProblem.Unreachable, 0)]
    public async Task A_list_that_cant_be_had_says_why(string how, LookupProblem problem, int status)
    {
        _http.Answer = r => how switch
        {
            "token-refused" => r.RequestUri!.AbsolutePath == "/token" ? FakeHttp.Json("""{"errors":[{"code":"DENIED"}]}""", HttpStatusCode.Forbidden)
                : FakeHttp.Challenge("https://ghcr.io/token", "ghcr.io", "repository:o/r:pull"),
            "still-401" => r.RequestUri!.AbsolutePath == "/token" ? FakeHttp.Json("""{"token":"t"}""")
                : FakeHttp.Challenge("https://ghcr.io/token", "ghcr.io", "repository:o/r:pull"),
            "no-challenge" => FakeHttp.Json("{}", HttpStatusCode.Unauthorized),
            "404" => FakeHttp.Json("""{"errors":[{"code":"NAME_UNKNOWN"}]}""", HttpStatusCode.NotFound),
            "500" => FakeHttp.Json("""{"errors":[{"message":"s3cret-route down"}]}""", HttpStatusCode.InternalServerError),
            "429" => FakeHttp.Json("{}", HttpStatusCode.TooManyRequests),
            "unreadable" => FakeHttp.Json("<html>"),
            _ => throw new HttpRequestException("No route to host"),
        };

        var list = await ListAsync("ghcr.io/o/r");

        Assert.Equal((problem, status), (list.Problem, list.Status));
        Assert.Empty(list.Tags);
        Assert.Equal(problem == LookupProblem.RateLimited ? Start.AddSeconds(60) : null, list.RetryAt);
    }

    [Fact]
    public async Task A_first_request_redirected_to_another_host_lists_there_without_the_saved_sign_in()
    {
        var signIn = new RegistrySignIn("someone", "s3cret-pass");
        _http.Answer = r =>
        {
            var url = r.RequestUri!;
            if (url.Host == "lscr.io")
            {
                // The handler followed lscr.io's redirect: the answer is ghcr.io's.
                var moved = FakeHttp.Challenge("https://ghcr.io/token", "ghcr.io", "repository:linuxserver/nginx:pull");
                moved.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://ghcr.io" + url.PathAndQuery);
                return moved;
            }
            if (url.AbsolutePath == "/token") return Auth(r) is null ? FakeHttp.Json("""{"token":"anon"}""") : FakeHttp.Json("{}", HttpStatusCode.BadRequest);
            if (Auth(r) != "Bearer anon") return FakeHttp.Json("{}", HttpStatusCode.NotFound);
            return url.Query.Contains("last=", StringComparison.Ordinal) ? Tags("1.27.2")
                : FakeHttp.Linked(Tags("1.27.3"), "</v2/linuxserver/nginx/tags/list?last=1.27.3&n=1000>; rel=\"next\"");
        };

        var list = await ListAsync("lscr.io/linuxserver/nginx", signIn);

        Assert.Equal(["1.27.3", "1.27.2"], list.Tags);
        Assert.Equal([
            "https://lscr.io/v2/linuxserver/nginx/tags/list?n=1000 -",
            "https://ghcr.io/token?service=ghcr.io&scope=repository%3Alinuxserver%2Fnginx%3Apull -",
            "https://ghcr.io/v2/linuxserver/nginx/tags/list?n=1000 Bearer anon",
            "https://ghcr.io/v2/linuxserver/nginx/tags/list?last=1.27.3&n=1000 Bearer anon",
        ], _http.Calls);
    }

    [Fact]
    public async Task A_signed_in_list_moved_to_another_host_goes_on_there_without_the_sign_in()
    {
        // The registry wants the sign-in itself; its answer to the signed-in request comes from another host (the handler
        // followed a redirect and dropped the header there), which names a next page of its own.
        var signIn = new RegistrySignIn("someone", "s3cret-pass");
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret-pass"));
        _http.Answer = r =>
        {
            var url = r.RequestUri!;
            if (url.Host == "mirror.lan") return Auth(r) is null ? Tags("1.27.2") : FakeHttp.BasicChallenge();
            if (Auth(r) != basic) return FakeHttp.BasicChallenge();
            var moved = FakeHttp.Linked(Tags("1.27.3"), "</v2/team/api/tags/list?last=1.27.3&n=1000>; rel=\"next\"");
            moved.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://mirror.lan" + url.PathAndQuery);
            return moved;
        };

        var list = await ListAsync("registry.lan/team/api", signIn);

        Assert.Equal(["1.27.3", "1.27.2"], list.Tags);
        Assert.Equal([
            "https://registry.lan/v2/team/api/tags/list?n=1000 -",
            $"https://registry.lan/v2/team/api/tags/list?n=1000 {basic}",
            "https://mirror.lan/v2/team/api/tags/list?last=1.27.3&n=1000 -",
        ], _http.Calls);
    }

    [Fact]
    public async Task An_image_read_redirected_to_another_host_asks_its_token_without_the_sign_in()
    {
        // New app reads an image's config before a pull (RemoteFacts): the registry's answer comes from another host, whose
        // 401 names its own token service. The sign-in saved for registry.lan isn't sent there.
        var signIn = new RegistrySignIn("someone", "s3cret-pass");
        _http.Answer = r =>
        {
            var url = r.RequestUri!;
            if (url.Host == "registry.lan")
            {
                var moved = FakeHttp.Challenge("https://auth.mirror.lan/token", "mirror.lan", "repository:team/api:pull");
                moved.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://mirror.lan" + url.PathAndQuery);
                return moved;
            }
            return url.AbsolutePath == "/token" ? FakeHttp.Json("""{"token":"anon"}""") : FakeHttp.Json("{}", HttpStatusCode.NotFound);
        };

        await Assert.ThrowsAnyAsync<Exception>(() => Client().ImageAsync(Ref("registry.lan/team/api:1.0"), signIn, HostPlatform.Default, CancellationToken.None));

        var token = Assert.Single(_http.Requests, r => r.Url.StartsWith("https://auth.mirror.lan/token", StringComparison.Ordinal));
        Assert.Null(token.Authorization);
    }

    [Fact]
    public async Task A_token_service_that_answers_too_much_is_read_no_further_than_its_cap()
    {
        var body = new Endless(64L << 20);
        _http.Answer = r => r.RequestUri!.AbsolutePath == "/token"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }
            : FakeHttp.Challenge("https://registry.e2e.test/token", "registry.e2e.test", "repository:team/api:pull");

        var list = await ListAsync("registry.e2e.test/team/api");

        Assert.Equal(LookupProblem.Failed, list.Problem);
        Assert.True(body.Served < 1 << 20, $"{body.Served} bytes of the token service's answer were read");
    }

    // An answer that goes on far past any cap (spaces), counting what was read of it.
    private sealed class Endless(long length) : Stream
    {
        public long Served { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - Served);
            Array.Fill(buffer, (byte)' ', offset, n);
            Served += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- through ImageService: ranked, kept per sign-in, problems in words ----

    [Fact]
    public async Task A_registrys_list_is_ranked_without_dates_and_kept_ten_minutes_per_sign_in()
    {
        _http.Answer = _ => Tags("1", "1.26", "1.26.5", "1.27", "1.27.2", "1.27.3", "1.28.0-rc1", "latest", "sha-4f9c2e1", "stable");
        var images = ImageKit.Images(new ImageKit.Engine(), _db, http: _http, time: _clock);

        var list = await images.TagsAsync("registry.e2e.test/team/api:1.27");

        Assert.Equal(("registry.e2e.test/team/api", "registry.e2e.test", false, "1.27", 9, 10, false),
            (list.Image, list.Registry, list.Dates, list.Default, list.Read, list.Total, list.More));
        var top = TagViews.For(list, "", false)[0];
        Assert.Equal(("1.27.3", true, true), (top.Tag, top.Row.Inferred, top.Recommended));
        Assert.Equal(["1.27", "1"], top.Follows);
        Assert.Equal(["latest", "stable"], list.Other.Select(r => r.Tag));

        await images.TagsAsync("registry.e2e.test/team/api");
        Assert.Single(_http.Requests);
        _clock.Now = Start.AddMinutes(10).AddSeconds(1);
        await images.TagsAsync("registry.e2e.test/team/api");
        Assert.Equal(2, _http.Requests.Count);
    }

    [Theory]
    [InlineData("500", LookupProblem.Failed, "registry.e2e.test didn't list registry.e2e.test/team/api's tags (HTTP 500).")]
    [InlineData("401", LookupProblem.NeedsSignIn, "registry.e2e.test wants a sign-in to list registry.e2e.test/team/api's tags: add one in Settings › Registries.")]
    [InlineData("404", LookupProblem.NotFound, "registry.e2e.test has no image called registry.e2e.test/team/api: check the name.")]
    [InlineData("429", LookupProblem.RateLimited, "registry.e2e.test asked Gitberth to wait before listing tags again.")]
    [InlineData("down", LookupProblem.Unreachable, "registry.e2e.test can't be reached from this host, so its tags can't be listed.")]
    public async Task A_registrys_problem_is_said_in_one_sentence_and_its_host_left_alone_as_it_asks(string how, LookupProblem problem, string message)
    {
        _http.Answer = _ => how switch
        {
            "500" => FakeHttp.Json("""{"errors":[{"message":"s3cret-route down"}]}""", HttpStatusCode.InternalServerError),
            "401" => FakeHttp.Json("{}", HttpStatusCode.Unauthorized),
            "404" => FakeHttp.Json("{}", HttpStatusCode.NotFound),
            "429" => FakeHttp.Json("{}", HttpStatusCode.TooManyRequests),
            _ => throw new HttpRequestException("Connection refused"),
        };
        var images = ImageKit.Images(new ImageKit.Engine(), _db, http: _http, time: _clock);

        var list = await images.TagsAsync("registry.e2e.test/team/api");

        Assert.Equal((problem, message), (list.Problem, list.Message));
        Assert.Empty(list.Versions);
        // Down for a minute, waiting as it asked, a missing repository two minutes: not asked again meanwhile.
        _clock.Now = Start.AddSeconds(30);
        Assert.Equal(problem, (await images.TagsAsync("registry.e2e.test/team/api")).Problem);
        Assert.Equal(how is "500" or "401" ? 2 : 1, _http.Requests.Count);
    }

    [Fact]
    public async Task A_saved_sign_in_is_kept_apart_from_the_anonymous_list_by_its_user_name()
    {
        var protector = ImageKit.Protector();
        var engine = new ImageKit.Engine();
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("someone:s3cret-pass"));
        _http.Answer = r => Auth(r) == basic ? Tags("2.0") : FakeHttp.BasicChallenge();
        var images = ImageKit.Images(engine, _db, protector, http: _http, time: _clock);

        Assert.Equal(LookupProblem.NeedsSignIn, (await images.TagsAsync("registry.lan/team/api")).Problem);
        Assert.True((await new RegistryService(_db, protector, engine).SaveAsync(new RegistryInput(null, "registry.lan", "someone", "s3cret-pass"))).Ok);
        var list = await images.TagsAsync("registry.lan/team/api");

        Assert.Equal(["2.0"], list.Versions.Select(r => r.Tag));
        Assert.DoesNotContain(_http.Requests, r => r.Authorization == basic && !r.Url.StartsWith("https://registry.lan/", StringComparison.Ordinal));
    }
}
