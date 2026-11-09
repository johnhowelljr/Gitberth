using System.Globalization;

namespace Gitberth.Network;

/// <summary>An app's health URL from inside the shared network; null when it answered 2xx or 3xx, else why not.</summary>
public interface IHealthProbe { Task<string?> CheckAsync(string host, int port, string path, CancellationToken ct); }

/// <summary>
/// The health URL check a deploy makes (DeployPipeline's), for the change engine: one GET of http://host:port/path through
/// the "health" client (5 s, redirects not followed, so a 3xx counts as an answer). The caller tries again within its window.
/// </summary>
public sealed class HealthProbe(IHttpClientFactory http) : IHealthProbe
{
    public async Task<string?> CheckAsync(string host, int port, string path, CancellationToken ct)
    {
        var url = Url(host, port, path);
        try
        {
            using var response = await http.CreateClient("health").GetAsync(url, ct);
            var status = (int)response.StatusCode;
            return status is >= 200 and < 400 ? null : $"{url} answered HTTP {status.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"{url} didn't answer ({ex.Message})";
        }
    }

    /// <summary>http://postgres:8080/health: the path gets its leading slash.</summary>
    public static string Url(string host, int port, string path) =>
        $"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}{(path.StartsWith('/') ? path : "/" + path)}";
}
