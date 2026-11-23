using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Backup;

namespace Gitberth.Docker;

/// <summary>
/// Save and restore's calls (<see cref="IBackupEngine"/>): volumes, a container's files as a tar (archive GET and PUT), a
/// command run in a container with its output and input streamed (exec), pause and unpause, and images saved and loaded.
/// Every stream is read as it comes (ResponseHeadersRead), never held whole.
/// </summary>
public sealed partial class DockerApi : IBackupEngine
{
    /// <summary>How long a stream (an archive, a dump, an image) may go without a byte before Gitberth gives up on it.</summary>
    public static readonly TimeSpan StreamIdle = TimeSpan.FromMinutes(5);

    /// <summary>GET volumes: every volume, as Docker lists them.</summary>
    public async Task<JsonArray> ListVolumesAsync(CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, "volumes", ct: ct))?["Volumes"] as JsonArray ?? [];

    /// <summary>GET system/df?type=volume: each volume's size, by name (an engine older than API 1.42 answers none).</summary>
    public async Task<IReadOnlyDictionary<string, long>> VolumeSizesAsync(CancellationToken ct = default)
    {
        if (await ApiVersionAsync(ct) < DiskUsageByType) return new Dictionary<string, long>();
        return ParseVolumeSizes(await SendAsync(HttpMethod.Get, "system/df?type=volume", timeout: BuildCacheReadTimeout, ct: ct));
    }

    /// <summary>Every volume's UsageData.Size in system/df's answer, by name; the ones not sized (-1) left out.</summary>
    internal static Dictionary<string, long> ParseVolumeSizes(string json)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var volume in (JsonNode.Parse(json)?["Volumes"] as JsonArray ?? []).OfType<JsonObject>())
            if (volume["Name"] is JsonValue n && n.TryGetValue<string>(out var name)
                && volume["UsageData"]?["Size"] is JsonValue size && size.TryGetValue<long>(out var bytes) && bytes >= 0)
                sizes[name] = bytes;
        return sizes;
    }

    /// <summary>GET networks, as Docker wrote them.</summary>
    public async Task<JsonArray> ListNetworksJsonAsync(CancellationToken ct = default) =>
        JsonNode.Parse(await SendAsync(HttpMethod.Get, "networks", ct: ct)) as JsonArray ?? [];

    /// <summary>POST containers/{id}/pause: its processes frozen until unpaused. One paused already counts as paused.</summary>
    public async Task PauseContainerAsync(string idOrName, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        await SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/pause", ct: ct);
    }

    /// <summary>POST containers/{id}/unpause.</summary>
    public async Task UnpauseContainerAsync(string idOrName, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        await SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(idOrName)}/unpause", ct: ct);
    }

    /// <summary>POST volumes/create. A volume of that name made already is Docker's own answer: it's returned as it is.</summary>
    public async Task CreateVolumeAsync(JsonObject body, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "volumes/create", body, ct: ct);

    /// <summary>GET containers/{id}/archive?path=: a tar of the path (a folder's entries start with its name), streamed.</summary>
    public async Task<Stream> GetArchiveAsync(string container, string path, CancellationToken ct = default) =>
        await GetStreamAsync($"containers/{Uri.EscapeDataString(container)}/archive?path={Uri.EscapeDataString(path)}", ct);

    /// <summary>
    /// PUT containers/{id}/archive?path=: the tar extracted into the path, which must exist. Ownership and modes are the
    /// tar's (the container has no user of its own set).
    /// </summary>
    public async Task PutArchiveAsync(string container, string path, Stream tar, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var target = $"containers/{Uri.EscapeDataString(container)}/archive?path={Uri.EscapeDataString(path)}&noOverwriteDirNonDir=true";
        using var request = new HttpRequestMessage(HttpMethod.Put, await VersionedAsync(target, ct))
        {
            Content = new StreamContent(new IdleStream(tar, StreamIdle), 128 * 1024),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
    }

    /// <summary>GET images/get?names=…: the images as docker save writes them (their names kept), streamed.</summary>
    public async Task<Stream> SaveImagesAsync(IReadOnlyList<string> names, CancellationToken ct = default) =>
        await GetStreamAsync("images/get?" + string.Join('&', names.Select(n => "names=" + Uri.EscapeDataString(n))), ct);

    /// <summary>POST images/load?quiet=1: a docker save tar loaded. Returns Docker's lines ("Loaded image: …"); its error throws.</summary>
    public async Task<string> LoadImagesAsync(Stream tar, CancellationToken ct = default)
    {
        using var expected = Expected?.ForApi(HttpMethod.Post, "images/load");
        using var request = new HttpRequestMessage(HttpMethod.Post, await VersionedAsync("images/load?quiet=1", ct))
        {
            Content = new StreamContent(new IdleStream(tar, StreamIdle), 128 * 1024),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new DockerApiException((int)response.StatusCode, ErrorMessage(text, response));
        var said = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParsePullLine(line) is not { } json) continue;
            if (json["errorDetail"]?["message"] is JsonValue e && e.TryGetValue<string>(out var error) || json["error"] is JsonValue e2 && e2.TryGetValue<string>(out error))
                throw new DockerApiException(0, error);
            if (json["stream"] is JsonValue s && s.TryGetValue<string>(out var stream) && stream.Trim().Length > 0) said.Add(stream.Trim());
        }
        return string.Join('\n', said);
    }

    /// <summary>POST images/create, read to its end: a line with an error throws it.</summary>
    public async Task PullImageAsync(string fromImage, string tagOrDigest, string? registryAuth, CancellationToken ct = default)
    {
        await foreach (var line in PullAsync(fromImage, tagOrDigest, null, registryAuth, ct))
            if (line["errorDetail"]?["message"] is JsonValue e && e.TryGetValue<string>(out var error) || line["error"] is JsonValue e2 && e2.TryGetValue<string>(out error))
                throw new DockerApiException(0, error);
    }

    /// <summary>GET distribution/{reference}/json, anonymously: the registry's digest for it; null when it hasn't it or can't say.</summary>
    public async Task<string?> RegistryDigestAsync(string reference, CancellationToken ct = default)
    {
        try
        {
            return (await DistributionAsync(reference, null, ct))["Descriptor"]?["digest"] is JsonValue d && d.TryGetValue<string>(out var digest) && digest.Length > 0
                ? digest
                : null;
        }
        catch (Exception ex) when (ex is DockerApiException or TimeoutException or HttpRequestException or JsonException)
        {
            return null;
        }
    }

    /// <summary>POST images/{name}/tag?repo=&amp;tag=.</summary>
    public async Task TagImageAsync(string image, string repository, string tag, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"images/{Uri.EscapeDataString(image)}/tag?repo={Uri.EscapeDataString(repository)}&tag={Uri.EscapeDataString(tag)}", ct: ct);

    /// <summary>
    /// docker exec, over the Engine API: the exec is made (its variables in its environment), then started on a connection
    /// of its own that Docker hands over (101 UPGRADED): stdin goes in, and its end is told by closing the connection's
    /// sending side; stdout and stderr come back in Docker's frames. Its exit code is asked once the output ends.
    /// </summary>
    public async Task<ExecResult> ExecAsync(string container, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? env, Stream? stdin,
        Stream? stdout, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var create = new JsonObject
        {
            ["AttachStdin"] = stdin is not null,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = false,
            ["Cmd"] = new JsonArray([.. command.Select(c => (JsonNode?)c)]),
        };
        if (env is { Count: > 0 }) create["Env"] = new JsonArray([.. env.Select(kv => (JsonNode?)$"{kv.Key}={kv.Value}")]);
        var made = JsonNode.Parse(await SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(container)}/exec", create, ct: ct));
        var id = made?["Id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new DockerApiException(0, $"Docker didn't say which exec it made in {container}.");

        var stderr = new TailBuffer(64 * 1024);
        // Given up on only when nothing moved either way for ExecIdle: a load (psql -q, mysql --force) prints nothing while
        // it takes its input, and a dump takes none while it prints.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(ExecIdle);
        void Moved()
        {
            try
            {
                idle.CancelAfter(ExecIdle);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        try
        {
            using var connection = await HijackAsync($"exec/{Uri.EscapeDataString(id)}/start", new JsonObject { ["Detach"] = false, ["Tty"] = false }, idle.Token);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(idle.Token);
            Exception? fed = null;
            var feeding = stdin is null ? Task.CompletedTask : Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[128 * 1024];
                    int n;
                    while ((n = await stdin.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        await connection.Stream.WriteAsync(buffer.AsMemory(0, n), stop.Token);
                        Moved();
                    }
                }
                catch (Exception ex)
                {
                    // The command ended before taking all of it (an error it printed says why), or the input failed.
                    fed = ex;
                }
                finally
                {
                    try
                    {
                        connection.Socket.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                    }
                }
            }, CancellationToken.None);
            try
            {
                await DemuxAsync(connection.Stream, stdout, stderr, Moved, idle.Token);
            }
            finally
            {
                await stop.CancelAsync();
                await feeding;
            }
            // What the input couldn't give (a damaged backup) is the caller's to know; a command that ended before taking all
            // of it says so with its exit code.
            ct.ThrowIfCancellationRequested();
            if (idle.IsCancellationRequested) throw new OperationCanceledException(idle.Token);
            if (fed is BackupFileException) throw fed;
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The command in {container} took nothing and printed nothing for {ExecIdle.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes.");
        }

        // The exit code is there once the output has ended; a moment later on a busy engine.
        for (var i = 0; ; i++)
        {
            var state = JsonNode.Parse(await SendAsync(HttpMethod.Get, $"exec/{Uri.EscapeDataString(id)}/json", ct: ct));
            var running = state?["Running"] is JsonValue r && r.TryGetValue<bool>(out var yes) && yes;
            if (!running && state?["ExitCode"] is JsonValue code && code.TryGetValue<int>(out var exit)) return new ExecResult(exit, stderr.Text);
            if (i >= 50) throw new TimeoutException($"The command in {container} didn't say how it ended.");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>How long a command run in a container may go with nothing moving either way (<see cref="StreamIdle"/>; shorter in tests).</summary>
    internal TimeSpan ExecIdle { get; init; } = StreamIdle;

    // Docker's frames for a container without a TTY: [type (1 stdout, 2 stderr), 0, 0, 0, u32 length][bytes]; each read
    // that brings something tells moved.
    private static async Task DemuxAsync(Stream raw, Stream? stdout, TailBuffer stderr, Action moved, CancellationToken ct)
    {
        var header = new byte[8];
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var got = await raw.ReadAtLeastAsync(header, 8, throwOnEndOfStream: false, ct);
            if (got == 0) return;
            if (got < 8) throw new IOException("Docker's output ended in the middle of a frame.");
            moved();
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            while (length > 0)
            {
                var n = await raw.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length)), ct);
                if (n == 0) throw new IOException("Docker's output ended in the middle of a frame.");
                moved();
                if (header[0] == 2) stderr.Add(buffer.AsSpan(0, n));
                else if (stdout is not null) await stdout.WriteAsync(buffer.AsMemory(0, n), ct);
                length -= n;
            }
        }
    }

    /// <summary>A connection Docker handed over after a request (exec's start): the stream and its socket, to close its sending side.</summary>
    private sealed record Hijacked(Stream Stream, Socket Socket) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    // POST <path> with Connection: Upgrade on a connection of its own, written by hand: what Docker sends after its answer's
    // headers is the command's raw stream, and the socket is Gitberth's to half-close.
    private async Task<Hijacked> HijackAsync(string path, JsonNode body, CancellationToken ct)
    {
        var versioned = await VersionedAsync(path, ct);
        var socket = await ConnectRawAsync(ct);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var json = Encoding.UTF8.GetBytes(body.ToJsonString());
            var head = Encoding.ASCII.GetBytes(
                $"POST /{versioned} HTTP/1.1\r\nHost: docker\r\nUser-Agent: Gitberth\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {json.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: Upgrade\r\nUpgrade: tcp\r\n\r\n");
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(json, ct);
            var (status, headers) = await ReadHeadAsync(stream, ct);
            if (status is 101 or 200) return new Hijacked(stream, socket);
            var length = headers.TryGetValue("content-length", out var l) && int.TryParse(l, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, 64 * 1024) : 0;
            var text = new byte[length];
            if (length > 0) await stream.ReadAtLeastAsync(text, length, throwOnEndOfStream: false, ct);
            using var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
            throw new DockerApiException(status, ErrorMessage(Encoding.UTF8.GetString(text), response));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // The status line and headers, a byte at a time: nothing of the stream after them is read.
    private static async Task<(int Status, Dictionary<string, string> Headers)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(256);
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new IOException("Docker closed the connection before answering.");
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines.Length > 0 ? lines[0].Split(' ', 3) : [];
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
            throw new IOException("Docker's answer wasn't readable.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
            if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        return (status, headers);
    }

    // A connection to the engine of its own, as the handler makes them: its unix socket, or tcp://host:port.
    private async Task<Socket> ConnectRawAsync(CancellationToken ct)
    {
        if (Endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var address = new Uri("http://" + Endpoint["tcp://".Length..].TrimEnd('/'));
            var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(address.Host, address.Port, ct);
                return tcp;
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
        var path = Endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) ? Endpoint["unix://".Length..]
            : Endpoint.StartsWith('/') ? Endpoint
            : throw new NotSupportedException($"Gitberth talks to Docker over unix:// or tcp://, not {Endpoint}.");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A GET whose answer is a stream: its headers awaited, its body handed on as it comes (and the answer let go with it).
    private async Task<Stream> GetStreamAsync(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, await VersionedAsync(path, ct));
        HttpResponseMessage? response = null;
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(DefaultTimeout);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timer.Token);
            if (!response.IsSuccessStatusCode)
                throw new DockerApiException((int)response.StatusCode, ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
            var body = await response.Content.ReadAsStreamAsync(ct);
            var owned = new IdleStream(body, StreamIdle, response, request);
            response = null;
            return owned;
        }
        finally
        {
            response?.Dispose();
            if (response is not null) request.Dispose();
        }
    }

    /// <summary>
    /// A stream that gives up (<see cref="TimeoutException"/>) when nothing comes, or nothing can be written, for
    /// <paramref name="idle"/>: a stalled engine never holds a save, and the containers it paused, for ever. It owns what it's
    /// given to dispose with it.
    /// </summary>
    internal sealed class IdleStream(Stream inner, TimeSpan idle, params IDisposable[] owned) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(idle);
            try
            {
                return await inner.ReadAsync(buffer, timer.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Docker sent nothing for {idle.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes.");
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(idle);
            try
            {
                await inner.WriteAsync(buffer, timer.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Docker took nothing for {idle.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes.");
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                foreach (var o in owned) o.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The last bytes of a command's stderr, as text: enough to say why it failed.</summary>
    private sealed class TailBuffer(int max)
    {
        private readonly List<byte> _bytes = [];

        public void Add(ReadOnlySpan<byte> data)
        {
            foreach (var b in data) _bytes.Add(b);
            if (_bytes.Count > max) _bytes.RemoveRange(0, _bytes.Count - max);
        }

        public string Text => Encoding.UTF8.GetString(_bytes.ToArray()).Trim();
    }
}
