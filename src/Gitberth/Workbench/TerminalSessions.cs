using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Docker;

namespace Gitberth.Workbench;

/// <summary>What a terminal's ticket holds: the service (a Compose app's), whether it runs as root, and its first size.</summary>
public sealed record TerminalDetails(string AppName, int? AppId, string? Service, bool Root, int Cols, int Rows);

/// <summary>Why a terminal can't be opened, or its ticket.</summary>
public sealed record TerminalTicketOutcome(string? Ticket, string? Problem);

/// <summary>
/// An app's terminal in the browser (B3): a shell in its running container (bash, else sh) with a TTY, bridged to the page's
/// WebSocket. The page first gets a one-time ticket from here (signed in, for a running app that isn't Gitberth's own),
/// then opens the socket with it. Opening and closing go in the audit log and the app's Activity (who, from where, which
/// shell and user; how long): what's typed is never recorded. A terminal nobody typed into for 30 minutes closes.
/// </summary>
/// <remarks>
/// The socket's protocol: binary messages are the terminal's bytes both ways; a text message from the page is
/// {"type":"resize","cols":…,"rows":…}; a text message to the page is {"type":"exit","code":…} or {"type":"error","message":…}
/// before Gitberth closes it.
/// </remarks>
public sealed class TerminalSessions(DockerApi api, AppCatalog catalog, WorkbenchTickets tickets, ActivityLog activity, ILogger<TerminalSessions> logger,
    Audit.AuditLog? audit = null)
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    /// <summary>The shells a terminal tries, in this order, by what's in the image.</summary>
    internal const string ShellProbe = "if command -v bash >/dev/null 2>&1; then command -v bash; else echo /bin/sh; fi";

    /// <summary>How long a terminal may go with no input before it closes (shorter in tests).</summary>
    internal TimeSpan IdleAfter { get; init; } = Idle;

    /// <summary>Whether an app can have a terminal at all: not Gitberth's own container, its tunnel or one of its helpers.</summary>
    public static bool Offered(AppSummary app) => !app.System && app.Role is not (Services.ContainerRole.Self or Services.ContainerRole.Helper);

    /// <summary>A ticket for the app's terminal (a Compose app's <paramref name="service"/>), for <paramref name="user"/>; or why not.</summary>
    public async Task<TerminalTicketOutcome> IssueAsync(string appKey, string? service, bool root, int cols, int rows, string user, CancellationToken ct = default)
    {
        var view = await catalog.GetAsync(appKey, "", ct);
        if (view is null) return new TerminalTicketOutcome(null, $"There's no app called {appKey}.");
        if (!Offered(view.Summary)) return new TerminalTicketOutcome(null, "Gitberth's own containers have no terminal here.");
        var container = view.Summary.Container;
        if (view.Row is { IsCompose: true } row && service is { Length: > 0 } s && s != row.WebService)
        {
            if (!Compose.ComposeNames.IsServiceName(s)) return new TerminalTicketOutcome(null, $"{s} isn't a service's name.");
            container = Compose.ComposeNames.Member(row.Slug, s);
        }
        else if (!view.Summary.Running) return new TerminalTicketOutcome(null, $"{view.Summary.Title} isn't running: start it, then open its terminal.");
        var details = new TerminalDetails(view.Summary.Title, view.Summary.AppId, view.Row?.IsCompose == true ? service : null, root, cols, rows);
        return new TerminalTicketOutcome(tickets.Issue(TicketKind.Terminal, user, appKey, container, details), null);
    }

    /// <summary>The terminal itself, on an accepted socket, until either side ends it.</summary>
    public async Task RunAsync(WebSocket socket, WorkbenchTicket ticket, string? ip, CancellationToken ct)
    {
        var details = (TerminalDetails)ticket.Details;
        var who = $"{ticket.User}{(ip is { Length: > 0 } ? $" from {ip}" : "")}";
        string shell;
        try
        {
            shell = await ShellAsync(ticket.Container, ct);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            await RefuseAsync(socket, Refusal(ticket.Container, ex.Message));
            return;
        }
        if (shell.Length == 0)
        {
            await RefuseAsync(socket, NoShell(ticket.Container));
            return;
        }
        var asUser = details.Root ? "root" : "the container's own user";
        TerminalExec exec;
        try
        {
            exec = await api.OpenTerminalAsync(ticket.Container, [shell], details.Root ? "0" : null, details.Cols, details.Rows, ct);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
        {
            await RefuseAsync(socket, Refusal(ticket.Container, ex.Message));
            return;
        }
        var opened = DateTime.UtcNow;
        Record("app.terminal.opened", $"Opened a terminal in {details.AppName} ({shell}, as {asUser}).", ticket, details, ip,
            [new("Shell", null, shell), new("User", null, asUser), new("Container", null, ticket.Container)]);
        await NoteAsync(ticket, details, $"Terminal opened by {who}: {shell}, as {asUser}.", Apps.ActivityTone.Neutral);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var idle = new IdleTimer(IdleAfter, session);
        using var sending = new SemaphoreSlim(1, 1);
        string ended;
        await using (exec)
        {
            var output = PumpOutAsync(exec, socket, sending, session.Token);
            var input = PumpInAsync(socket, exec, idle, session.Token);
            var first = await Task.WhenAny(output, input, idle.Fired);
            ended = first == idle.Fired ? "idle" : first == output ? "shell" : "page";
            if (ended == "shell")
            {
                int? exitCode = null;
                try
                {
                    exitCode = await api.ExecExitCodeAsync(exec.Id, CancellationToken.None);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
                {
                }
                await SayAsync(socket, sending, "exit", exitCode?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            else if (ended == "idle")
            {
                await SayAsync(socket, sending, "error", $"Closed after {Minutes(IdleAfter)} without input. Open it again to go on.");
            }
            // The page answers Gitberth's close with its own, which ends the input's pump; a page that doesn't is let go.
            await CloseOutputAsync(socket, sending);
            if (await Task.WhenAny(input, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)) != input) await session.CancelAsync();
            // Docker keeps an exec running when its connection goes: the shell is hung up as a terminal would be, with
            // Ctrl-C for what runs in it and Ctrl-D for the shell itself, before its input ends.
            if (ended != "shell") await HangUpAsync(exec);
            exec.EndInput();
            await session.CancelAsync();
            await Settle(output);
            await Settle(input);
        }
        var took = DateTime.UtcNow - opened;
        var how = ended switch { "idle" => $", after {Minutes(IdleAfter)} without input", "shell" => ": the shell ended", _ => "" };
        Record("app.terminal.closed", $"Closed the terminal in {details.AppName} after {Took(took)}{how}.", ticket, details, ip,
            [new("Open for", null, Took(took))]);
        await NoteAsync(ticket, details, $"Terminal closed after {Took(took)}{how}.", Apps.ActivityTone.Neutral);
    }

    // bash when the image has it, else /bin/sh; empty when it has no shell at all.
    private async Task<string> ShellAsync(string container, CancellationToken ct)
    {
        using var stdout = new MemoryStream();
        var result = await api.ExecAsync(container, ["/bin/sh", "-c", ShellProbe], null, null, stdout, ct);
        if (result.ExitCode is 126 or 127 || NoShellSaid(result.StdErr)) return "";
        if (result.ExitCode != 0) throw new InvalidOperationException(result.StdErr.Trim() is { Length: > 0 } said ? said : $"the shell ended with exit code {result.ExitCode}");
        var shell = Encoding.UTF8.GetString(stdout.ToArray()).Trim().Split('\n')[0].Trim();
        return shell.StartsWith('/') ? shell : "/bin/sh";
    }

    // Docker's words for an image without the shell: the exec couldn't start /bin/sh.
    private static bool NoShellSaid(string text) =>
        text.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase) || text.Contains("executable file not found", StringComparison.OrdinalIgnoreCase);

    private static string NoShell(string container) => $"There's no shell in {container}'s image (no /bin/sh or bash), so it has no terminal.";

    /// <summary>Why a shell couldn't be opened, in a sentence: the container isn't running, its image has no shell, or Docker's own words.</summary>
    internal static string Refusal(string container, string said) =>
        said.Contains("is not running", StringComparison.OrdinalIgnoreCase) || said.Contains("No such container", StringComparison.OrdinalIgnoreCase)
            ? $"{container} isn't running, so it has no terminal now. Start it, then open the terminal again."
            : NoShellSaid(said) ? NoShell(container)
            : $"Docker couldn't open a shell in {container}: {said.Trim().TrimEnd('.')}.";

    // Said to the page, and the socket closed: nothing was opened.
    private static async Task RefuseAsync(WebSocket socket, string why)
    {
        await SayAsync(socket, null, "error", why);
        using var sending = new SemaphoreSlim(1, 1);
        await CloseOutputAsync(socket, sending);
    }

    private static async Task PumpOutAsync(TerminalExec exec, WebSocket socket, SemaphoreSlim sending, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await exec.Stream.ReadAsync(buffer, ct)) > 0)
        {
            await sending.WaitAsync(ct);
            try
            {
                await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
            finally
            {
                sending.Release();
            }
        }
    }

    private async Task PumpInAsync(WebSocket socket, TerminalExec exec, IdleTimer idle, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var text = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var got = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (got.MessageType == WebSocketMessageType.Close) return;
            if (got.MessageType == WebSocketMessageType.Binary)
            {
                idle.Reset();
                await exec.Stream.WriteAsync(buffer.AsMemory(0, got.Count), ct);
                continue;
            }
            // A control message: the page's terminal was resized. At most 4 KB of it is read.
            if (text.Length < 4096) text.Write(buffer, 0, got.Count);
            if (!got.EndOfMessage) continue;
            var said = Encoding.UTF8.GetString(text.ToArray());
            text.SetLength(0);
            if (Resize(said) is { } size)
            {
                try
                {
                    await api.ResizeTerminalAsync(exec.Id, size.Cols, size.Rows, ct);
                }
                catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException)
                {
                    logger.LogDebug(ex, "Couldn't resize the terminal in {Container}", exec.Id);
                }
            }
        }
    }

    /// <summary>A resize message's columns and rows; null for anything else.</summary>
    internal static (int Cols, int Rows)? Resize(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject o || (string?)o["type"] != "resize") return null;
            return o["cols"] is JsonValue c && c.TryGetValue<int>(out var cols) && o["rows"] is JsonValue r && r.TryGetValue<int>(out var rows) && cols > 0 && rows > 0
                ? (Math.Min(cols, 1000), Math.Min(rows, 500))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A text message to the page ({"type":"exit","code":…} or {"type":"error","message":…}), between the output's messages.
    private static async Task SayAsync(WebSocket socket, SemaphoreSlim? sending, string type, string text)
    {
        var message = new JsonObject { ["type"] = type };
        if (type == "exit") message["code"] = text.Length == 0 ? null : int.Parse(text, CultureInfo.InvariantCulture);
        else message["message"] = text;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (sending is not null) await sending.WaitAsync(wait.Token);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, endOfMessage: true, wait.Token);
            }
            finally
            {
                sending?.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task CloseOutputAsync(WebSocket socket, SemaphoreSlim sending)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await sending.WaitAsync(wait.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", wait.Token);
            }
            finally
            {
                sending.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task HangUpAsync(TerminalExec exec)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await exec.Stream.WriteAsync(new byte[] { 3 }, wait.Token);
            await Task.Delay(100, wait.Token);
            await exec.Stream.WriteAsync(new byte[] { 4 }, wait.Token);
            await exec.Stream.FlushAsync(wait.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task Settle(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private void Record(string action, string summary, WorkbenchTicket ticket, TerminalDetails details, string? ip, IReadOnlyList<Audit.AuditChange> changes) =>
        audit?.Record(new Audit.AuditEntry(action, summary)
        {
            AppId = details.AppId, AppName = details.AppName, Container = ticket.Container, Target = details.Service,
            Actor = Audit.AuditActor.User(ticket.User, ip), Changes = changes,
        });

    private async Task NoteAsync(WorkbenchTicket ticket, TerminalDetails details, string text, Apps.ActivityTone tone)
    {
        try
        {
            await activity.AddAsync(ticket.Container, details.AppId, ActivityKind.Terminal, text, tone);
        }
        catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Couldn't write the terminal of {Container} down on its Activity", ticket.Container);
        }
    }

    internal static string Took(TimeSpan t) => t.TotalMinutes < 1 ? $"{Math.Max(1, (int)t.TotalSeconds)} s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalHours} h {t.Minutes} min";

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} minutes" : $"{(int)t.TotalSeconds} seconds";

    /// <summary>Fires once no input came for its span; each keystroke starts it again.</summary>
    private sealed class IdleTimer : IDisposable
    {
        private readonly TaskCompletionSource _fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Timer _timer;
        private readonly TimeSpan _span;

        public IdleTimer(TimeSpan span, CancellationTokenSource session)
        {
            _span = span;
            _timer = new Timer(_ => _fired.TrySetResult(), null, span, Timeout.InfiniteTimeSpan);
            session.Token.Register(() => _timer.Change(Timeout.Infinite, Timeout.Infinite));
        }

        public Task Fired => _fired.Task;

        public void Reset()
        {
            try
            {
                _timer.Change(_span, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}

/// <summary>
/// The terminal's WebSocket, /terminal?ticket=…: signed in, from one of Gitberth's own pages (its Origin is Gitberth's own
/// host), and with a ticket the page got a moment before (once, within 30 seconds). Anything else is refused before the
/// socket opens.
/// </summary>
public static class TerminalEndpoint
{
    public const string Path = "/terminal";

    public static void Map(WebApplication app) => app.Map(Path, HandleAsync);

    private static async Task HandleAsync(HttpContext context, WorkbenchTickets tickets, TerminalSessions sessions)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, "This is the terminal's WebSocket: open the terminal on an app's page.");
            return;
        }
        if (context.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } user })
        {
            await PlainAsync(context, StatusCodes.Status401Unauthorized, "You're signed out. Sign in again, then open the terminal.");
            return;
        }
        if (!SameOrigin(context.Request))
        {
            await PlainAsync(context, StatusCodes.Status403Forbidden, "Refused: a terminal opens only from Gitberth's own pages.");
            return;
        }
        if (tickets.Take(context.Request.Query["ticket"], TicketKind.Terminal, user) is not { } ticket)
        {
            await PlainAsync(context, StatusCodes.Status403Forbidden, "The terminal's ticket is missing, used or out of date: open the terminal again.");
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await sessions.RunAsync(socket, ticket, Audit.AuditCircuitHandler.ClientIp(context), context.RequestAborted);
    }

    /// <summary>
    /// Whether the request comes from Gitberth's own pages: its Origin's host and port are the host it was sent to (or the
    /// one a proxy says it was). A browser always sends Origin with a WebSocket, and a page of another site can't set it.
    /// </summary>
    internal static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out var o) || o.Scheme is not ("http" or "https")) return false;
        var authority = o.IsDefaultPort ? o.Host : o.Authority;
        bool Same(string? host) => host is { Length: > 0 } h && (string.Equals(h, o.Authority, StringComparison.OrdinalIgnoreCase) || string.Equals(h, authority, StringComparison.OrdinalIgnoreCase));
        return Same(request.Host.Value) || Same(request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim());
    }

    private static async Task PlainAsync(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text);
    }
}
