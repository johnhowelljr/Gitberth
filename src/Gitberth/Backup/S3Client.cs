using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Gitberth.Backup;

/// <summary>An S3-compatible bucket as the settings name it: where it is, how it's addressed, and the folder in it.</summary>
/// <param name="Endpoint">The service's address (https://s3.us-east-1.amazonaws.com for AWS S3).</param>
/// <param name="PathStyle">endpoint/bucket/key; else bucket.endpoint/key (AWS S3's own).</param>
/// <param name="Prefix">The folder in the bucket, without slashes at either end; empty for its top.</param>
public sealed record S3Target(Uri Endpoint, string Region, string Bucket, string Prefix, bool PathStyle)
{
    /// <summary>The settings' bucket: AWS S3 when no endpoint is set, path-style everywhere else unless they say.</summary>
    public static S3Target Of(AutoBackupConfig c)
    {
        var region = string.IsNullOrWhiteSpace(c.S3Region) ? "us-east-1" : c.S3Region.Trim();
        var endpoint = string.IsNullOrWhiteSpace(c.S3Endpoint) ? new Uri($"https://s3.{region}.amazonaws.com") : new Uri(c.S3Endpoint.Trim().TrimEnd('/'));
        var bucket = c.S3Bucket!.Trim();
        return new S3Target(endpoint, region, bucket, (c.S3Prefix ?? "").Trim().Trim('/'), c.S3PathStyle ?? PathStyleFor(c));
    }

    /// <summary>
    /// Path-style unless set: on everywhere but AWS S3, which answers bucket.s3.region.amazonaws.com (a bucket with dots
    /// can't, under its certificate).
    /// </summary>
    public static bool PathStyleFor(AutoBackupConfig c)
    {
        var aws = string.IsNullOrWhiteSpace(c.S3Endpoint)
            || Uri.TryCreate(c.S3Endpoint.Trim(), UriKind.Absolute, out var u) && u.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase);
        return !aws || c.S3Bucket?.Contains('.') == true;
    }

    /// <summary>"s3://backups/gitberth at s3.us-west-004.backblazeb2.com".</summary>
    public static string Describe(AutoBackupConfig c)
    {
        var prefix = (c.S3Prefix ?? "").Trim().Trim('/');
        var host = Uri.TryCreate(c.S3Endpoint?.Trim(), UriKind.Absolute, out var u) ? u.Authority : $"AWS S3 ({(string.IsNullOrWhiteSpace(c.S3Region) ? "us-east-1" : c.S3Region.Trim())})";
        return $"s3://{c.S3Bucket?.Trim()}{(prefix.Length > 0 ? "/" + prefix : "")} at {host}";
    }

    /// <summary>The object's key for a file name: the prefix, then the name.</summary>
    public string Key(string name) => Prefix.Length > 0 ? $"{Prefix}/{name}" : name;
}

/// <summary>An S3 answer that isn't a success, with its code and message (never a key or a signature).</summary>
public sealed class S3Exception(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// S3's API, signed with AWS Signature Version 4 (no SDK): objects put, read, listed and removed, and big ones uploaded in
/// parts, each part's payload signed. Works with AWS S3, Backblaze B2, Wasabi, MinIO and Cloudflare R2.
/// </summary>
public sealed class S3Client(HttpClient http, S3Target target, string accessKey, string secretKey, TimeProvider? time = null)
{
    /// <summary>Its HttpClient's name (long timeouts: a part is 32 MiB).</summary>
    public const string ClientName = "backup-s3";

    /// <summary>The SHA-256 of nothing, a GET's payload hash.</summary>
    public const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // ---- objects ----

    public async Task PutAsync(string key, byte[] body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, key, [], body, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>The object's bytes into <paramref name="into"/>; with <paramref name="range"/>, only those ("bytes=0-63").</summary>
    public async Task GetAsync(string key, Stream into, CancellationToken ct, string? range = null)
    {
        using var response = await SendAsync(HttpMethod.Get, key, [], null, ct, range);
        await EnsureAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(into, 128 * 1024, ct);
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, [], null, ct);
        await EnsureAsync(response, ct);
    }

    /// <summary>Every object under the prefix (ListObjectsV2, page by page): its key, size and when it was written.</summary>
    public async Task<List<(string Key, long Size, DateTime Modified)>> ListAsync(string prefix, CancellationToken ct)
    {
        var all = new List<(string, long, DateTime)>();
        string? token = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null) query["continuation-token"] = token;
            using var response = await SendAsync(HttpMethod.Get, null, query, null, ct);
            await EnsureAsync(response, ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
            foreach (var c in xml.Root?.Elements(ns + "Contents") ?? [])
                if (c.Element(ns + "Key")?.Value is { } key && long.TryParse(c.Element(ns + "Size")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                    all.Add((key, size, DateTime.TryParse(c.Element(ns + "LastModified")?.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : DateTime.MinValue));
            token = xml.Root?.Element(ns + "IsTruncated")?.Value == "true" ? xml.Root.Element(ns + "NextContinuationToken")?.Value : null;
        }
        while (token is not null);
        return all;
    }

    // ---- multipart ----

    public async Task<string> StartUploadAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, key, new SortedDictionary<string, string>(StringComparer.Ordinal) { ["uploads"] = "" },
            ReadOnlyMemory<byte>.Empty, ct);
        await EnsureAsync(response, ct);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return xml.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value is { Length: > 0 } id
            ? id
            : throw new S3Exception(response.StatusCode, "NoUploadId", "The bucket didn't say which upload it started.");
    }

    /// <summary>One part (its payload signed); returns its ETag.</summary>
    public async Task<string> UploadPartAsync(string key, string uploadId, int number, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["partNumber"] = number.ToString(CultureInfo.InvariantCulture), ["uploadId"] = uploadId };
        using var response = await SendAsync(HttpMethod.Put, key, query, body, ct);
        await EnsureAsync(response, ct);
        return response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out var tags) ? tags.First() : throw new S3Exception(response.StatusCode, "NoETag", "The bucket didn't say what it got."));
    }

    public async Task CompleteUploadAsync(string key, string uploadId, IReadOnlyList<string> etags, CancellationToken ct)
    {
        var xml = new XElement("CompleteMultipartUpload", etags.Select((tag, i) =>
            new XElement("Part", new XElement("PartNumber", (i + 1).ToString(CultureInfo.InvariantCulture)), new XElement("ETag", tag))));
        var body = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        using var response = await SendAsync(HttpMethod.Post, key, new SortedDictionary<string, string>(StringComparer.Ordinal) { ["uploadId"] = uploadId }, body, ct);
        await EnsureAsync(response, ct);
        // S3 may say 200 and put its error in the body.
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Contains("<Error>", StringComparison.Ordinal)) throw ErrorOf(response.StatusCode, text);
    }

    public async Task AbortUploadAsync(string key, string uploadId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, new SortedDictionary<string, string>(StringComparer.Ordinal) { ["uploadId"] = uploadId }, null, ct);
        await EnsureAsync(response, ct);
    }

    // ---- signing ----

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string? key, SortedDictionary<string, string> query, ReadOnlyMemory<byte>? body,
        CancellationToken ct, string? range = null)
    {
        var (uri, path) = Address(key, query);
        var request = new HttpRequestMessage(method, uri);
        var payload = body is { } b ? Convert.ToHexStringLower(SHA256.HashData(b.Span)) : EmptyHash;
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}",
            ["x-amz-content-sha256"] = payload,
            ["x-amz-date"] = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        };
        if (range is not null) headers["range"] = range;
        request.Headers.TryAddWithoutValidation("Authorization", Authorization(method.Method, path, CanonicalQuery(query), headers, payload, accessKey, secretKey, target.Region));
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payload);
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        if (range is not null) request.Headers.TryAddWithoutValidation("Range", range);
        if (body is { } content)
        {
            request.Content = new ReadOnlyMemoryContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // The request's address and its canonical path (each segment percent-encoded once, as S3 signs it).
    private (Uri Uri, string Path) Address(string? key, SortedDictionary<string, string> query)
    {
        var keyPath = key is null ? "" : string.Join('/', key.Split('/').Select(Encode));
        var path = target.PathStyle ? $"/{Encode(target.Bucket)}/{keyPath}" : "/" + keyPath;
        if (key is null) path = target.PathStyle ? $"/{Encode(target.Bucket)}" : "/";
        var authority = target.PathStyle ? target.Endpoint.Authority : $"{target.Bucket}.{target.Endpoint.Authority}";
        var q = CanonicalQuery(query);
        var uri = new Uri($"{target.Endpoint.Scheme}://{authority}{path}{(q.Length > 0 ? "?" + q : "")}",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        return (uri, path);
    }

    /// <summary>The query as SigV4 signs it: sorted by name, every name and value percent-encoded.</summary>
    internal static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join('&', query.Select(kv => (Encode(kv.Key), Encode(kv.Value))).OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => $"{p.Item1}={p.Item2}"));

    /// <summary>RFC 3986 percent-encoding as SigV4 wants it: everything but letters, digits and -._~, in upper-case hex.</summary>
    internal static string Encode(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The Authorization header of AWS Signature Version 4: the canonical request (method, path, query, the headers given
    /// and the payload's hash), signed with the key HMAC-SHA256 derives from the secret, the day, the region and "s3".
    /// </summary>
    internal static string Authorization(string method, string path, string query, SortedDictionary<string, string> headers, string payloadHash,
        string accessKey, string secretKey, string region)
    {
        var amzDate = headers["x-amz-date"];
        var day = amzDate[..8];
        var signed = string.Join(';', headers.Keys);
        var canonical = $"{method}\n{path}\n{query}\n{string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n"))}\n{signed}\n{payloadHash}";
        var scope = $"{day}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), day);
        key = Hmac(key, region);
        key = Hmac(key, "s3");
        key = Hmac(key, "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, toSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signed}, Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw ErrorOf(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static S3Exception ErrorOf(HttpStatusCode status, string body)
    {
        string code = ((int)status).ToString(CultureInfo.InvariantCulture), message = $"it answered {(int)status} {status}";
        try
        {
            var xml = XDocument.Parse(body);
            code = xml.Root?.Element("Code")?.Value ?? code;
            message = xml.Root?.Element("Message")?.Value ?? message;
        }
        catch (System.Xml.XmlException)
        {
        }
        return new S3Exception(status, code, message);
    }
}

/// <summary>
/// An S3-compatible bucket as a destination: a backup goes up as a multipart upload in parts of 32 MiB as it's made (one
/// part in memory at a time), aborted when anything fails, so nothing half-written stays.
/// </summary>
internal sealed class S3Destination(HttpClient http, S3Target target, string accessKey, string secretKey) : IBackupDestination
{
    /// <summary>A part's size: 32 MiB, so 10,000 parts hold 320 GB.</summary>
    internal const int PartBytes = 32 << 20;

    private readonly S3Client _s3 = new(http, target, accessKey, secretKey);

    internal int PartSize { get; init; } = PartBytes;

    public string Where => $"s3://{target.Bucket}{(target.Prefix.Length > 0 ? "/" + target.Prefix : "")} at {target.Endpoint.Authority}";

    public async Task WriteAsync(string name, Stream content, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        var key = target.Key(name);
        string uploadId;
        try
        {
            uploadId = await _s3.StartUploadAsync(key, ct);
        }
        catch (Exception ex) when (ex is S3Exception or HttpRequestException)
        {
            throw Said("Starting the upload", ex);
        }
        var etags = new List<string>();
        var buffer = new byte[PartSize];
        try
        {
            while (true)
            {
                var filled = await content.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                if (filled > 0 || etags.Count == 0)
                {
                    if (etags.Count >= 10_000) throw new DestinationException("The backup is larger than an S3 upload takes (10,000 parts of 32 MiB).");
                    etags.Add(await _s3.UploadPartAsync(key, uploadId, etags.Count + 1, buffer.AsMemory(0, filled), ct));
                }
                if (filled < buffer.Length) break;
            }
            await _s3.CompleteUploadAsync(key, uploadId, etags, ct);
        }
        catch (Exception ex)
        {
            try
            {
                await _s3.AbortUploadAsync(key, uploadId, CancellationToken.None);
            }
            catch (Exception abort) when (abort is S3Exception or HttpRequestException or TaskCanceledException)
            {
                // The bucket's own lifecycle rule (or the next run) clears an upload left open.
            }
            if (ex is S3Exception or HttpRequestException) throw Said("Uploading", ex);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    public async Task<IReadOnlyList<DestinationFile>> ListAsync(bool headers, CancellationToken ct)
    {
        List<(string Key, long Size, DateTime Modified)> objects;
        try
        {
            objects = await _s3.ListAsync(target.Prefix.Length > 0 ? target.Prefix + "/" : "", ct);
        }
        catch (Exception ex) when (ex is S3Exception or HttpRequestException or System.Xml.XmlException)
        {
            throw Said("Listing", ex);
        }
        var files = new List<DestinationFile>();
        foreach (var (key, size, modified) in objects)
        {
            var name = target.Prefix.Length > 0 ? key[(target.Prefix.Length + 1)..] : key;
            if (name.Contains('/') || !AutoDestinations.Ours(name)) continue;
            byte[]? header = null;
            if (headers && name.EndsWith(BackupFormat.Extension, StringComparison.Ordinal))
            {
                var head = new MemoryStream();
                try
                {
                    await _s3.GetAsync(key, head, ct, "bytes=0-63");
                    header = head.ToArray();
                }
                catch (Exception ex) when (ex is S3Exception or HttpRequestException)
                {
                }
            }
            files.Add(new DestinationFile(name, size, modified == DateTime.MinValue ? null : modified, header));
        }
        return files;
    }

    public async Task ReadAsync(string name, Stream into, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        try
        {
            await _s3.GetAsync(target.Key(name), into, ct);
        }
        catch (Exception ex) when (ex is S3Exception or HttpRequestException)
        {
            throw Said("Reading " + name, ex);
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        AutoDestinations.CheckName(name);
        try
        {
            await _s3.DeleteAsync(target.Key(name), ct);
        }
        catch (Exception ex) when (ex is S3Exception or HttpRequestException)
        {
            throw Said("Removing " + name, ex);
        }
    }

    // A sentence about what the bucket said (its code and message) or why it wasn't reached.
    private DestinationException Said(string what, Exception ex) => new(ex switch
    {
        S3Exception { Code: "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch" } s3 =>
            $"{what} at {Where} failed: the bucket refused the access key or secret key ({s3.Code}).",
        S3Exception { Code: "NoSuchBucket" } => $"{what} at {Where} failed: there's no bucket called {target.Bucket}.",
        S3Exception s3 => $"{what} at {Where} failed: {s3.Code}, {s3.Message.TrimEnd('.')}.",
        _ => $"{what} at {Where} failed: {target.Endpoint.Authority} couldn't be reached ({ex.Message.TrimEnd('.')}).",
    }, ex);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
