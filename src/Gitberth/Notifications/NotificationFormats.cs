using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Gitberth.Notifications;

/// <summary>An HTTP request to make: what each web channel is sent, as data, so each format is tested on its own.</summary>
public sealed record HttpNotice(string Body, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>A mail to send.</summary>
public sealed record MailNotice(string Subject, string Body);

/// <summary>A notice shaped for each channel: Discord's embed, Slack's message, ntfy's headers, a webhook's JSON, a mail.</summary>
public static class NotificationFormats
{
    public const string Sender = "Gitberth";

    /// <summary>Discord's limits: an embed's title and description.</summary>
    private const int DiscordTitle = 256, DiscordText = 4000;

    public static HttpNotice Discord(Notice n)
    {
        var embed = new JsonObject
        {
            ["title"] = Cut(n.Title, DiscordTitle),
            ["description"] = Cut(n.Text, DiscordText),
            ["color"] = n.Level switch { NoticeLevel.Good => 0x2EA043, NoticeLevel.Warning => 0xD29922, NoticeLevel.Critical => 0xDA3633, _ => 0x4C8DFF },
            ["timestamp"] = n.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["footer"] = new JsonObject { ["text"] = Sender },
        };
        if (n.Link is not null) embed["url"] = n.Link;
        var body = new JsonObject
        {
            ["username"] = Sender,
            ["embeds"] = new JsonArray(embed),
            // A notice never pings anyone, whatever an app's name or an error says.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
        };
        return new HttpNotice(body.ToJsonString(), "application/json", []);
    }

    public static HttpNotice Slack(Notice n)
    {
        var text = $"{Icon(n.Level)} *{SlackEscape(n.Title)}*\n{SlackEscape(n.Text)}";
        if (n.Link is not null) text += $"\n<{n.Link}|Open in Gitberth>";
        var body = new JsonObject { ["text"] = text, ["unfurl_links"] = false, ["unfurl_media"] = false };
        return new HttpNotice(body.ToJsonString(), "application/json", []);
    }

    /// <summary>
    /// ntfy: the text as the body, the rest in headers (Title, Priority, Tags, Click). A header carries only ASCII, so a
    /// title with anything else (an app named "Café", a preview's "·") is sent RFC 2047-encoded, which ntfy reads.
    /// </summary>
    public static HttpNotice Ntfy(Notice n, string? token)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Title", HeaderValue(n.Title)),
            new("Priority", n.Level switch { NoticeLevel.Critical => "4", NoticeLevel.Warning => "4", _ => "3" }),
            new("Tags", n.Level switch { NoticeLevel.Critical => "rotating_light", NoticeLevel.Warning => "warning", NoticeLevel.Good => "white_check_mark", _ => "information_source" }),
        };
        if (n.Link is not null) headers.Add(new("Click", n.Link));
        if (!string.IsNullOrWhiteSpace(token)) headers.Add(new("Authorization", $"Bearer {token.Trim()}"));
        return new HttpNotice(n.Text, "text/plain; charset=utf-8", headers);
    }

    /// <summary>A webhook: one JSON object with everything, for Home Assistant, n8n or a script of your own.</summary>
    public static HttpNotice Webhook(Notice n, string? token)
    {
        var body = new JsonObject
        {
            ["event"] = EventName(n.Event),
            ["level"] = n.Level.ToString().ToLowerInvariant(),
            ["title"] = n.Title,
            ["text"] = n.Text,
            ["url"] = n.Link,
            ["app"] = n.App.Length == 0 ? null : n.App,
            ["appId"] = n.AppId,
            ["container"] = n.Container,
            ["preview"] = n.Preview,
            ["deployment"] = n.DeploymentId,
            ["commit"] = n.Commit,
            ["trigger"] = n.Trigger,
            ["time"] = n.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        };
        var headers = new List<KeyValuePair<string, string>> { new("X-Gitberth-Event", EventName(n.Event)) };
        if (!string.IsNullOrWhiteSpace(token)) headers.Add(new("Authorization", $"Bearer {token.Trim()}"));
        return new HttpNotice(body.ToJsonString(), "application/json", headers);
    }

    public static MailNotice Mail(Notice n)
    {
        var body = new StringBuilder();
        body.AppendLine(n.Title).AppendLine().AppendLine(n.Text);
        if (n.Link is not null) body.AppendLine().AppendLine(n.Link);
        body.AppendLine().AppendLine("-- ")
            .AppendLine($"Sent by Gitberth on {n.At.ToUniversalTime().ToString("MMM d, yyyy HH:mm", CultureInfo.InvariantCulture)} UTC. Change what it sends in Settings › Notifications.");
        return new MailNotice($"[Gitberth] {OneLine(n.Title)}", body.ToString());
    }

    /// <summary>The event as a webhook names it: deploy.failed, container.crashed, …</summary>
    public static string EventName(NotifyEvent e) => e switch
    {
        NotifyEvent.DeployFailed => "deploy.failed",
        NotifyEvent.DeploySucceeded => "deploy.succeeded",
        NotifyEvent.Crashed => "container.crashed",
        NotifyEvent.Unhealthy => "container.unhealthy",
        NotifyEvent.Healthy => "container.healthy",
        _ => "test",
    };

    private static string Icon(NoticeLevel level) => level switch
    {
        NoticeLevel.Good => ":white_check_mark:",
        NoticeLevel.Warning => ":warning:",
        NoticeLevel.Critical => ":rotating_light:",
        _ => ":information_source:",
    };

    // Slack reads &, < and > as markup: escaped, so an app's name or an error can't make a link or a mention.
    private static string SlackEscape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    internal static string HeaderValue(string text)
    {
        var line = OneLine(text);
        return line.All(c => c is >= ' ' and <= '~') ? line : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(line))}?=";
    }

    private static string OneLine(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
