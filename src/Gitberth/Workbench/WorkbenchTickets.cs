using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Gitberth.Workbench;

/// <summary>What a one-time ticket is for.</summary>
public enum TicketKind { Terminal, Download, Upload }

/// <summary>
/// A one-time ticket (B3): who it was given to (the signed-in person, as the audit log names them), for what, and what it
/// lets them reach (an app's container, and a terminal's or a file's details). It's good once, for 30 seconds.
/// </summary>
public sealed record WorkbenchTicket(TicketKind Kind, string User, string AppKey, string Container, object Details, DateTime ExpiresAt);

/// <summary>
/// The one-time tickets a page asks the server for before it opens a terminal's WebSocket or downloads or uploads files:
/// random (256 bits), good once and for 30 seconds, and only for the person they were given to. A WebSocket can't carry
/// the page's form token, and a link can't either: the ticket stands in for it, so another site can't make the browser
/// open one.
/// </summary>
public sealed class WorkbenchTickets(TimeProvider? clock = null)
{
    public static readonly TimeSpan Life = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, WorkbenchTicket> _tickets = new(StringComparer.Ordinal);

    public string Issue(TicketKind kind, string user, string appKey, string container, object details)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (token, old) in _tickets)
            if (old.ExpiresAt <= now) _tickets.TryRemove(token, out _);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[ticket] = new WorkbenchTicket(kind, user, appKey, container, details, now + Life);
        return ticket;
    }

    /// <summary>The ticket's details, once: null when there's no such ticket, it's used or out of date, or it's another's or for something else.</summary>
    public WorkbenchTicket? Take(string? token, TicketKind kind, string? user)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100 || !_tickets.TryRemove(token, out var ticket)) return null;
        return ticket.Kind == kind && ticket.User == user && ticket.ExpiresAt > _clock.GetUtcNow().UtcDateTime ? ticket : null;
    }
}
