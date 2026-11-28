using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Gitberth.Docker;

/// <summary>
/// A shell in a container with a terminal (B3): the exec's ID, and its raw stream both ways (a TTY's output has no frames,
/// stdout and stderr come as one). Disposing it closes the connection, which hangs the shell up.
/// </summary>
public sealed class TerminalExec(string id, Stream stream, Socket socket) : IAsyncDisposable
{
    public string Id { get; } = id;
    public Stream Stream { get; } = stream;

    /// <summary>Tells the shell there's no more input (the connection's sending side closes).</summary>
    public void EndInput()
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

public sealed partial class DockerApi
{
    /// <summary>
    /// docker exec -it, over the Engine API: <paramref name="command"/> in <paramref name="container"/> with a terminal of
    /// <paramref name="cols"/> by <paramref name="rows"/> (TERM=xterm-256color), as <paramref name="user"/> (null: the
    /// container's own user), started on a connection of its own that Docker hands over.
    /// </summary>
    public async Task<TerminalExec> OpenTerminalAsync(string container, IReadOnlyList<string> command, string? user, int cols, int rows, CancellationToken ct = default)
    {
        await ChangingAsync(ct);
        var create = new JsonObject
        {
            ["AttachStdin"] = true,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Tty"] = true,
            ["ConsoleSize"] = new JsonArray(Math.Clamp(rows, 2, 500), Math.Clamp(cols, 10, 1000)),
            ["Env"] = new JsonArray("TERM=xterm-256color"),
            ["Cmd"] = new JsonArray([.. command.Select(c => (JsonNode?)c)]),
        };
        if (user is { Length: > 0 }) create["User"] = user;
        var made = JsonNode.Parse(await SendAsync(HttpMethod.Post, $"containers/{Uri.EscapeDataString(container)}/exec", create, ct: ct));
        var id = made?["Id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new DockerApiException(0, $"Docker didn't say which exec it made in {container}.");
        var connection = await HijackAsync($"exec/{Uri.EscapeDataString(id)}/start",
            new JsonObject { ["Detach"] = false, ["Tty"] = true, ["ConsoleSize"] = new JsonArray(Math.Clamp(rows, 2, 500), Math.Clamp(cols, 10, 1000)) }, ct);
        return new TerminalExec(id, connection.Stream, connection.Socket);
    }

    /// <summary>POST exec/{id}/resize: the terminal's new size.</summary>
    public async Task ResizeTerminalAsync(string execId, int cols, int rows, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"exec/{Uri.EscapeDataString(execId)}/resize?h={Math.Clamp(rows, 2, 500).ToString(CultureInfo.InvariantCulture)}&w={Math.Clamp(cols, 10, 1000).ToString(CultureInfo.InvariantCulture)}", ct: ct);

    /// <summary>The exec's exit code once it has ended; null while it runs (or when Docker can't say).</summary>
    public async Task<int?> ExecExitCodeAsync(string execId, CancellationToken ct = default)
    {
        var state = JsonNode.Parse(await SendAsync(HttpMethod.Get, $"exec/{Uri.EscapeDataString(execId)}/json", ct: ct));
        var running = state?["Running"] is JsonValue r && r.TryGetValue<bool>(out var yes) && yes;
        return !running && state?["ExitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : null;
    }
}
