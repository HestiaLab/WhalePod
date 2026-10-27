using System.Globalization;

using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MimeKit;

namespace WhalePod.Infrastructure.Email;

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string TextBody,
    string HtmlBody);

public enum EmailSendOutcome
{
    Sent,
    HardBounce,
    TransientFailure
}

public sealed record EmailSendResult(
    EmailSendOutcome Outcome,
    string? ErrorCode = null);

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Development email transport accepted a message.");
        return Task.FromResult(new EmailSendResult(EmailSendOutcome.Sent));
    }
}

public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpEmailOptions _smtp = options.Value.Smtp;

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(
            _smtp.FromName,
            _smtp.FromAddress ?? throw new InvalidOperationException("Email:Smtp:FromAddress is required.")));
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody
        }.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = Math.Max(1, _smtp.TimeoutSeconds) * 1000
        };
        try
        {
            await client.ConnectAsync(
                _smtp.Host ?? throw new InvalidOperationException("Email:Smtp:Host is required."),
                _smtp.Port,
                ParseSecurity(_smtp.Security),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(_smtp.UserName))
            {
                await client.AuthenticateAsync(_smtp.UserName, _smtp.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return new EmailSendResult(EmailSendOutcome.Sent);
        }
        catch (SmtpCommandException exception)
        {
            var statusCode = (int)exception.StatusCode;
            var outcome = statusCode is 550 or 551 or 553
                ? EmailSendOutcome.HardBounce
                : EmailSendOutcome.TransientFailure;
            logger.LogWarning(
                "SMTP delivery failed with status {StatusCode}.",
                statusCode);
            return new EmailSendResult(
                outcome,
                $"smtp_{statusCode.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception exception) when (exception is SmtpProtocolException
            or MailKit.Security.AuthenticationException
            or InvalidOperationException
            or IOException)
        {
            logger.LogWarning(
                "SMTP delivery failed with {ErrorType}.",
                exception.GetType().Name);
            return new EmailSendResult(EmailSendOutcome.TransientFailure, "smtp_transport");
        }
    }

    private static SecureSocketOptions ParseSecurity(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "starttlswhenavailable" => SecureSocketOptions.StartTlsWhenAvailable,
            "ssl" or "sslconnect" or "implicit" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            "auto" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.StartTls
        };
    }
}