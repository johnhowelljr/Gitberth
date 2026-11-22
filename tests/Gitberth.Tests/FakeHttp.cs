using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Gitberth.Tests;

/// <summary>
/// The network as a test says it answers, for Docker Hub's API and registries: every request seen (by which named
/// client, with its User-Agent, Accept and Authorization), and the test's answer to each. Nothing leaves the process.
/// </summary>
internal sealed class FakeHttp : IHttpClientFactory
{
    public sealed record Seen(string Client, string Url, string? UserAgent, string? Accept, string? Authorization);

    private readonly List<Seen> _requests = [];

    /// <summary>The answer to a request; throwing is a network that doesn't answer (an HttpRequestException without a status).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => Json("{}", HttpStatusCode.NotFound);

    public List<Seen> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public List<string> Urls => Requests.Select(r => r.Url).ToList();

    /// <summary>Each request as "url authorization" ("-" for none).</summary>
    public List<string> Calls => Requests.Select(r => $"{r.Url} {r.Authorization ?? "-"}").ToList();

    public HttpClient CreateClient(string name) => new(new Handler(this, name));

    private sealed class Handler(FakeHttp http, string client) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (http._requests)
                http._requests.Add(new Seen(client, request.RequestUri!.AbsoluteUri, Header(request.Headers.UserAgent.ToString()),
                    Header(request.Headers.Accept.ToString()), request.Headers.Authorization?.ToString()));
            var response = http.Answer(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }

        private static string? Header(string value) => value.Length == 0 ? null : value;
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>An answer gzipped, as Docker Hub sends one: the handler in the tests doesn't decompress it.</summary>
    public static HttpResponseMessage Gzip(string body)
    {
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(Encoding.UTF8.GetBytes(body));
        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>A registry's 401 naming its token service.</summary>
    public static HttpResponseMessage Challenge(string realm, string service, string scope)
    {
        var response = Json("""{"errors":[{"code":"UNAUTHORIZED"}]}""", HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"{realm}\",service=\"{service}\",scope=\"{scope}\"");
        return response;
    }

    /// <summary>A registry's 401 that wants the sign-in itself.</summary>
    public static HttpResponseMessage BasicChallenge()
    {
        var response = Json("{}", HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd("Basic realm=\"registry\"");
        return response;
    }

    /// <summary>The answer with a Link header (tags/list's next page).</summary>
    public static HttpResponseMessage Linked(HttpResponseMessage response, string link)
    {
        response.Headers.TryAddWithoutValidation("Link", link);
        return response;
    }
}
