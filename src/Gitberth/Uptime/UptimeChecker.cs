using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Gitberth.Uptime;

/// <summary>The certificates the uptime checks' TLS handshakes saw: when each host's expires.</summary>
public sealed class UptimeCertificates
{
    private readonly ConcurrentDictionary<string, DateTime> _expires = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the host's certificate expires, as the last handshake with it saw; null when none was seen.</summary>
    public DateTime? ExpiresOf(string host) => _expires.TryGetValue(host, out var at) ? at : null;

    internal void Saw(string host, DateTime notAfterUtc)
    {
        if (host.Length > 0) _expires[host] = notAfterUtc;
    }
}

/// <summary>
/// One uptime check: a GET of the address with the "uptime" client (10 seconds, no redirects, a new connection each time,
/// the certificate checked as a browser would and its expiry kept), and what the answer means. Up is an answer below 500
/// that isn't Gitberth's own down page; anything else is down, with a class people understand.
/// </summary>
public sealed class UptimeChecker(IHttpClientFactory http, TimeProvider? time = null)
{
    public const string ClientName = "uptime";

    /// <summary>How long a check waits for an answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public const string NotReachable = "Not reachable";
    public const string TimedOut = "Timed out";
    public const string CertificateProblem = "Certificate problem";
    public const string DownPage = "Showing the down page";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<UptimeResult> CheckAsync(int appId, string address, CancellationToken ct)
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.ConnectionClose = true;
            request.Headers.UserAgent.ParseAdd("Gitberth-Uptime/1.3");
            request.Headers.Accept.ParseAdd("text/html");
            request.Headers.Accept.ParseAdd("*/*");
            using var response = await http.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var downPage = response.Headers.TryGetValues(Public.PublicPages.PageHeader, out var page) && page.Contains("down", StringComparer.OrdinalIgnoreCase);
            var (up, problem) = Classify((int)response.StatusCode, downPage, null);
            return new UptimeResult(appId, at, up, (int)response.StatusCode, ms, problem, address);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException or AuthenticationException
                                   && !ct.IsCancellationRequested)
        {
            return new UptimeResult(appId, at, false, null, null, Classify(null, false, ex).Problem, address);
        }
    }

    /// <summary>
    /// Whether an answer (or the error instead of one) counts as up, and if not, why, in the words the pages and the
    /// notifications use: "Not reachable", "Server error 502", "Showing the down page", "Certificate problem", "Timed out".
    /// </summary>
    public static (bool Up, string? Problem) Classify(int? status, bool downPage, Exception? error)
    {
        if (error is not null) return (false, Describe(error));
        if (status is not int code) return (false, NotReachable);
        if (downPage) return (false, DownPage);
        return code >= 500 ? (false, $"Server error {code.ToString(CultureInfo.InvariantCulture)}") : (true, null);
    }

    private static string Describe(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return CertificateProblem;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return CertificateProblem;
            if (e is TimeoutException) return TimedOut;
        }
        return error is TaskCanceledException or OperationCanceledException ? TimedOut : NotReachable;
    }

    /// <summary>
    /// The handler behind the "uptime" client: no redirects, a new connection for every check (so a check really reaches the
    /// host), and the server's certificate checked as usual (an invalid one fails the check) with its expiry kept per host.
    /// With <paramref name="connectTo"/> (GITBERTH_UPTIME_CONNECT_TO, a diagnostic setting), every HTTPS check connects to
    /// that host and port instead, while the request's host name stays the Host and the TLS server name.
    /// </summary>
    public static SocketsHttpHandler Handler(UptimeCertificates certificates, string? connectTo)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = Timeout,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, _, errors) =>
        {
            if (sender is SslStream stream && certificate is X509Certificate2 cert) certificates.Saw(stream.TargetHostName, cert.NotAfter.ToUniversalTime());
            return errors == SslPolicyErrors.None;
        };
        if (ConnectTo(connectTo) is var (host, port))
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                EndPoint target = context.InitialRequestMessage.RequestUri?.Scheme == Uri.UriSchemeHttps ? new DnsEndPoint(host, port) : context.DnsEndPoint;
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(target, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        return handler;
    }

    /// <summary>host[:port] (443 when there's none); null when the value is empty or can't be read.</summary>
    internal static (string Host, int Port)? ConnectTo(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        var colon = v.LastIndexOf(':');
        if (colon > 0 && !v.EndsWith(']') && int.TryParse(v[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536)
            return (v[..colon].Trim('[', ']'), port);
        return colon < 0 || v.EndsWith(']') ? (v.Trim('[', ']'), 443) : null;
    }
}
