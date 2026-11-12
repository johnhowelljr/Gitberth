using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;

namespace Gitberth.Notifications;

/// <summary>Sends a mail through a mail server. An interface so tests don't need one.</summary>
public interface IMailer
{
    Task SendAsync(Destination channel, MailNotice mail, CancellationToken ct);
}

/// <summary>.NET's mail client: plain, or STARTTLS (port 587); the certificate is checked like any other.</summary>
public sealed class SmtpMailer : IMailer
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(Destination channel, MailNotice mail, CancellationToken ct)
    {
        // SmtpClient is what .NET has without another dependency; it does STARTTLS and sign-in, which is all this needs.
        using var client = new SmtpClient(channel.SmtpHost, channel.SmtpPort)
        {
            EnableSsl = channel.Security == SmtpSecurity.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)Timeout.TotalMilliseconds,
        };
        if (!string.IsNullOrWhiteSpace(channel.SmtpUser)) client.Credentials = new NetworkCredential(channel.SmtpUser, channel.SmtpPassword);
        using var message = new MailMessage { From = new MailAddress(channel.From!), Subject = mail.Subject, Body = mail.Body, BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        foreach (var to in DestinationText.Addresses(channel.To)) message.To.Add(to);
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>Why a delivery failed, in words for Settings: never the channel's URL, which is its key.</summary>
public sealed class NotifyException(string message, bool transient) : Exception(message)
{
    /// <summary>Worth trying again in a moment: the service was busy, down or slow, not refusing.</summary>
    public bool Transient { get; } = transient;
}

/// <summary>Delivers one notice to one channel.</summary>
public sealed class NotificationSender(IHttpClientFactory http, IMailer mailer)
{
    public const string ClientName = "notify";

    public async Task SendAsync(Destination channel, Notice notice, CancellationToken ct)
    {
        if (channel.Kind == DestinationKind.Email)
        {
            await SendMailAsync(channel, notice, ct);
            return;
        }
        var request = channel.Kind switch
        {
            DestinationKind.Discord => NotificationFormats.Discord(notice),
            DestinationKind.Slack => NotificationFormats.Slack(notice),
            DestinationKind.Ntfy => NotificationFormats.Ntfy(notice, channel.Token),
            _ => NotificationFormats.Webhook(notice, channel.Token),
        };
        var who = channel.Kind == DestinationKind.Webhook ? "The webhook" : DestinationText.KindName(channel.Kind);
        using var message = new HttpRequestMessage(HttpMethod.Post, channel.Url) { Content = new StringContent(request.Body, Encoding.UTF8) };
        message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType);
        foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(ClientName).SendAsync(message, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NotifyException($"{who} didn't answer in time.", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new NotifyException($"{who} couldn't be reached: {Reason(ex)}", transient: true);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return;
            var code = (int)response.StatusCode;
            throw code switch
            {
                401 or 403 => new NotifyException($"{who} refused it ({code}): {(channel.Kind is DestinationKind.Ntfy or DestinationKind.Webhook ? "check the token" : "the webhook was deleted or its URL is wrong")}.", false),
                404 => new NotifyException($"{who} says the URL doesn't exist (404): " + (channel.Kind is DestinationKind.Discord or DestinationKind.Slack
                    ? "the webhook was deleted, or the URL is wrong." : "check the URL."), false),
                429 => new NotifyException($"{who} says too many messages were sent (429). It's tried again in a moment.", true),
                >= 500 => new NotifyException($"{who} had a problem ({code}). It's tried again in a moment.", true),
                _ => new NotifyException($"{who} didn't take it ({code} {response.ReasonPhrase}).", false),
            };
        }
    }

    private async Task SendMailAsync(Destination channel, Notice notice, CancellationToken ct)
    {
        try
        {
            await mailer.SendAsync(channel, NotificationFormats.Mail(notice), ct);
        }
        catch (SmtpFailedRecipientsException ex)
        {
            throw new NotifyException($"The mail server refused {string.Join(", ", ex.InnerExceptions.Select(e => e.FailedRecipient))}.", false);
        }
        catch (SmtpFailedRecipientException ex)
        {
            throw new NotifyException($"The mail server refused {ex.FailedRecipient}.", false);
        }
        catch (SmtpException ex) when (ex.StatusCode is SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst or SmtpStatusCode.CommandNotImplemented
                                           || ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotifyException($"The mail server refused the sign-in or the connection: {Plain(ex)}", false);
        }
        catch (SmtpException ex) when (ex.InnerException is IOException or SocketException or WebException)
        {
            throw new NotifyException($"The mail server {channel.SmtpHost} couldn't be reached on port {channel.SmtpPort}: {Plain(ex.InnerException!)}", true);
        }
        catch (SmtpException ex)
        {
            throw new NotifyException($"The mail server said: {Plain(ex)}", ex.StatusCode is SmtpStatusCode.ServiceNotAvailable or SmtpStatusCode.MailboxBusy or SmtpStatusCode.InsufficientStorage);
        }
        catch (System.Security.Authentication.AuthenticationException ex)
        {
            throw new NotifyException($"The mail server's TLS certificate isn't trusted: {Plain(ex)}", false);
        }
        catch (InvalidOperationException ex)
        {
            throw new NotifyException($"The mail couldn't be sent: {Plain(ex)}", false);
        }
    }

    private static string Reason(HttpRequestException ex) => ex.InnerException switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => "its name isn't known (DNS).",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "nothing answers there.",
        System.Security.Authentication.AuthenticationException => "its TLS certificate isn't trusted.",
        { } inner => Plain(inner),
        _ => Plain(ex),
    };

    private static string Plain(Exception ex) => ex.Message.TrimEnd('.') + ".";
}
