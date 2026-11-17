using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MiBitacora.Data;

namespace MiBitacora.Services;

public static class SmtpDelivery
{
    private static async Task ConnectAsync(SmtpClient client, MailOptions options, CancellationToken ct)
    {
        if (options.Delivery != "Smtp" || !options.IsValid(false)) throw new JournalInputException("La configuración SMTP no es válida.");
        // Require encryption; never fall back to cleartext if STARTTLS is unavailable.
        var security = options.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(options.Host, options.Port, security, ct);
        await client.AuthenticateAsync(options.Username, options.Password, ct);
    }

    public static async Task CheckConnectionAsync(MailOptions options, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var client = new SmtpClient { Timeout = 20_000 };
        await ConnectAsync(client, options, deadline.Token);
        await client.DisconnectAsync(true, deadline.Token);
        // Authentication probe only: no MAIL FROM, recipient, message or journal content is sent.
    }

    internal static MimeMessage CreateMessage(MailOptions options, TransactionalEmailRow row)
    {
        var message = new MimeMessage { MessageId = $"{row.Id:N}@mibitacora.ar", Subject = row.Subject,
            Date = new DateTimeOffset(DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc)), Body = new TextPart("plain") { Text = row.Body } };
        message.From.Add(new MailboxAddress("Mi Bitácora · COGNITIVA", new System.Net.Mail.MailAddress(options.From).Address));
        message.To.Add(MailboxAddress.Parse(row.Recipient));
        message.ReplyTo.Add(MailboxAddress.Parse(BusinessIdentity.SupportEmail));
        return message;
    }

    public static async Task SendAsync(MailOptions options, TransactionalEmailRow row, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var message = CreateMessage(options, row);
        using var client = new SmtpClient { Timeout = 20_000 };
        await ConnectAsync(client, options, deadline.Token);
        await client.SendAsync(new SmtpSendRequest(message), deadline.Token);
        // Once SMTP accepted the message, a failure sending QUIT must not schedule another delivery.
        try { await client.DisconnectAsync(true, deadline.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SmtpProtocolException) { }
    }
}
