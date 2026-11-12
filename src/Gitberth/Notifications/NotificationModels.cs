using System.Net.Mail;

namespace Gitberth.Notifications;

/// <summary>Where a notification goes.</summary>
public enum DestinationKind { Email, Discord, Slack, Ntfy, Webhook }

/// <summary>How Gitberth talks to a mail server. TLS from the first byte (port 465) isn't something .NET's mail client speaks.</summary>
public enum SmtpSecurity { StartTls, None }

/// <summary>What a notification is about. Settings › Notifications turns each kind on or off; a test is always sent.</summary>
public enum NotifyEvent { DeployFailed, DeploySucceeded, Crashed, Unhealthy, Healthy, Test }

/// <summary>How bad it is: the color of a Discord embed, an ntfy priority, the word in a webhook's JSON.</summary>
public enum NoticeLevel { Good, Info, Warning, Critical }

/// <summary>
/// One place notifications go: a mail address (through a mail server), a Discord or Slack channel (its webhook URL), an
/// ntfy topic, or any URL that takes a JSON POST. Saved encrypted as a whole: a Discord or Slack webhook URL is itself the
/// key to the channel, and so are tokens and the mail password.
/// </summary>
public sealed record Destination
{
    public string Id { get; init; } = NewId();
    public DestinationKind Kind { get; init; }
    /// <summary>What the list calls it; the kind's name when empty.</summary>
    public string Name { get; init; } = "";
    /// <summary>The webhook URL (Discord, Slack, Webhook) or the topic's URL (ntfy).</summary>
    public string Url { get; init; } = "";
    /// <summary>ntfy's access token, or a webhook's bearer token: sent as Authorization. Optional.</summary>
    public string? Token { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;
    public string? SmtpUser { get; init; }
    public string? SmtpPassword { get; init; }
    /// <summary>The sender's address, "Gitberth &lt;gitberth@example.com&gt;" or just the address.</summary>
    public string? From { get; init; }
    /// <summary>One or more addresses, separated by commas, semicolons or new lines.</summary>
    public string? To { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public string Title => Name.Trim() is { Length: > 0 } name ? name : DestinationText.KindName(Kind);
}

/// <summary>
/// One message: what happened, to which app, and where to read more. Built by Gitberth from a deploy or a container event,
/// then shaped for each channel by <see cref="NotificationFormats"/>.
/// </summary>
/// <param name="Title">One line: "Deploy of web failed".</param>
/// <param name="Text">A sentence or two: the error, the commit, what happens next.</param>
/// <param name="Link">The deploy log or the app's page on Gitberth; null when Gitberth's address isn't known yet.</param>
/// <param name="App">The app's name as Gitberth shows it; empty for a test.</param>
/// <param name="Container">Its container; null for a test.</param>
public sealed record Notice(NotifyEvent Event, NoticeLevel Level, string Title, string Text, string? Link, string App, string? Container, DateTime At)
{
    public int? AppId { get; init; }
    public int? DeploymentId { get; init; }
    public string? Commit { get; init; }
    public string? Trigger { get; init; }
    public bool Preview { get; init; }
}

/// <summary>How a delivery went, kept per channel (in memory) for Settings to show.</summary>
public sealed record Delivery(DateTime At, bool Ok, string Title, string? Error);

/// <summary>Names and checks for channels, as Settings shows them.</summary>
public static class DestinationText
{
    public static string KindName(DestinationKind kind) => kind switch
    {
        DestinationKind.Email => "Email",
        DestinationKind.Discord => "Discord",
        DestinationKind.Slack => "Slack",
        DestinationKind.Ntfy => "ntfy",
        _ => "Webhook",
    };

    /// <summary>
    /// Where it goes, safe to show: the mail addresses, or the URL's host with its secret part left out (a Discord or
    /// Slack webhook URL is the channel's key; an ntfy topic is a shared secret too).
    /// </summary>
    public static string Target(Destination c) => c.Kind switch
    {
        DestinationKind.Email => $"{string.Join(", ", Addresses(c.To))} via {c.SmtpHost}",
        DestinationKind.Ntfy when Uri.TryCreate(c.Url, UriKind.Absolute, out var u) => $"{u.Host}/{Hide(u.AbsolutePath.Trim('/'))}",
        _ when Uri.TryCreate(c.Url, UriKind.Absolute, out var u) => $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : $":{u.Port}")}/…",
        _ => "",
    };

    // The first two characters of an ntfy topic, enough to tell two apart.
    private static string Hide(string topic) => topic.Length <= 2 ? "••••" : topic[..2] + "••••";

    /// <summary>The addresses in a To field: split on commas, semicolons and new lines, blanks left out.</summary>
    public static List<string> Addresses(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>What's wrong with a channel as it would be saved, or null when it can be used.</summary>
    public static string? Problem(Destination c)
    {
        if (c.Name.Length > 60) return "Keep the name to 60 characters.";
        switch (c.Kind)
        {
            case DestinationKind.Email:
                if (string.IsNullOrWhiteSpace(c.SmtpHost)) return "Enter the mail server, like smtp.gmail.com.";
                if (c.SmtpHost.Trim().Contains(' ') || c.SmtpHost.Contains("://", StringComparison.Ordinal)) return "Enter only the mail server's name, like smtp.gmail.com.";
                if (c.SmtpPort is < 1 or > 65535) return "The port is a number from 1 to 65535.";
                if (c.SmtpPort == 465)
                    return "Port 465 starts TLS before anything else, which Gitberth's mail client doesn't do. Use port 587 with STARTTLS: every big provider has it.";
                if (string.IsNullOrWhiteSpace(c.SmtpUser) != string.IsNullOrWhiteSpace(c.SmtpPassword))
                    return string.IsNullOrWhiteSpace(c.SmtpUser) ? "Enter the user name for the password, or leave both empty." : "Enter the password for the user name, or leave both empty.";
                if (!MailAddressOk(c.From)) return "Enter the address the mail comes from, like gitberth@example.com.";
                var to = Addresses(c.To);
                if (to.Count == 0) return "Enter at least one address to send to.";
                if (to.FirstOrDefault(a => !MailAddressOk(a)) is { } bad) return $"{bad} isn't a mail address.";
                if (to.Count > 20) return "Send to at most 20 addresses.";
                return null;
            case DestinationKind.Discord:
                return UrlProblem(c.Url, httpsOnly: true, "Discord's webhook URL, from the channel's Integrations › Webhooks",
                    u => u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal) ? null
                        : "That isn't a Discord webhook URL: it has /api/webhooks/ in it. Copy it from the channel's Integrations › Webhooks.");
            case DestinationKind.Slack:
                return UrlProblem(c.Url, httpsOnly: true, "Slack's incoming webhook URL", _ => null);
            case DestinationKind.Ntfy:
                return UrlProblem(c.Url, httpsOnly: false, "the topic's URL, like https://ntfy.sh/your-topic",
                    u => u.AbsolutePath.Trim('/').Length == 0 ? "Add the topic to the URL: https://ntfy.sh/your-topic." : null);
            default:
                return UrlProblem(c.Url, httpsOnly: false, "the URL to POST to", _ => null);
        }
    }

    private static string? UrlProblem(string? url, bool httpsOnly, string what, Func<Uri, string?> more)
    {
        if (string.IsNullOrWhiteSpace(url)) return $"Enter {what}.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0)
            return $"Enter {what}, starting with https://.";
        if (httpsOnly && u.Scheme != "https") return "It starts with https://.";
        if (u.UserInfo.Length > 0) return "Leave the user name and password out of the URL; put a token in the Token field.";
        return more(u);
    }

    private static bool MailAddressOk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            return new MailAddress(text.Trim()).Address.Contains('@');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
