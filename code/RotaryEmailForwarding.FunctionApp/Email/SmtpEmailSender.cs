using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class SmtpEmailSender(AppConfiguration configuration) : IEmailSender
{
    private const int MaxSendAttempts = 3;
    private const int RetryBaseDelayMilliseconds = 500;
    private const int SmtpTimeoutMilliseconds = 30_000;

    public async Task<EmailSendResult> SendAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
    {
        if (message.Recipients.Count == 0)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "NoRecipients",
                "Outbound message has no recipients.");
        }

        if (!EmailAddressUtility.IsUsable(configuration.SendingEmailAddress))
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "MissingSendingEmailAddress",
                "sendingEmailAddress is required.");
        }

        if (!configuration.IsProduction
            && string.IsNullOrWhiteSpace(configuration.NonProductionSafeRecipient)
            && !configuration.AllowUnsafeNonProductionEmail)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "UnsafeNonProductionEmailBlocked",
                "Non-production email requires nonProductionSafeRecipient or allowUnsafeNonProductionEmail=true.");
        }

        if (string.IsNullOrWhiteSpace(configuration.SendingEmailPassword))
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "MissingSmtpPassword",
                "sendingEmailPassword is required.");
        }

        for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
        {
            try
            {
                using var emailClient = new SmtpClient
                {
                    Timeout = SmtpTimeoutMilliseconds
                };
                var emailToSend = BuildMimeMessage(
                    message,
                    configuration.SendingEmailAddress,
                    EffectiveRecipients(message.Recipients));

                await emailClient.ConnectAsync(
                    configuration.MailHost,
                    configuration.MailPort,
                    ResolveSocketOptions(configuration.MailSecurityMode),
                    cancellationToken);
                await emailClient.AuthenticateAsync(
                    configuration.SendingEmailAddress,
                    configuration.SendingEmailPassword,
                    cancellationToken);
                await emailClient.SendAsync(emailToSend, cancellationToken);

                // SendAsync completed, so the provider accepted the message. A failure while
                // politely closing the connection must not cause the message to be sent twice.
                try
                {
                    await emailClient.DisconnectAsync(true, cancellationToken);
                }
                catch (Exception exception) when (IsHandledSmtpException(exception))
                {
                    // Disposal will close the connection.
                }

                return EmailSendResult.Success("SmtpAccepted", "Message accepted by SMTP provider.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsHandledSmtpException(exception))
            {
                var result = Classify(exception);
                if (result.Status != OutboundEmailAttemptStatus.RetryableFailed || attempt == MaxSendAttempts)
                {
                    return result;
                }

                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("SMTP retry loop completed without a result.");
    }

    public static EmailSendResult Classify(Exception exception)
    {
        var message = exception.Message;
        var lowerMessage = message.ToLowerInvariant();

        if (lowerMessage.Contains("quota", StringComparison.Ordinal)
            || lowerMessage.Contains("daily", StringComparison.Ordinal)
            || lowerMessage.Contains("too many", StringComparison.Ordinal)
            || lowerMessage.Contains("rate", StringComparison.Ordinal)
            || lowerMessage.Contains("max", StringComparison.Ordinal))
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.QuotaExceeded,
                "QuotaExceeded",
                message);
        }

        if (lowerMessage.Contains("auth", StringComparison.Ordinal)
            || lowerMessage.Contains("credential", StringComparison.Ordinal)
            || lowerMessage.Contains("password", StringComparison.Ordinal)
            || lowerMessage.Contains("mailbox unavailable", StringComparison.Ordinal)
            || lowerMessage.Contains("invalid recipient", StringComparison.Ordinal))
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "TerminalSmtpFailure",
                message);
        }

        if (exception is FormatException)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "InvalidEmailAddress",
                message);
        }

        if (exception is SmtpCommandException smtpException
            && smtpException.ErrorCode is SmtpErrorCode.SenderNotAccepted
                or SmtpErrorCode.RecipientNotAccepted)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                smtpException.ErrorCode.ToString(),
                message);
        }

        if (exception is SmtpCommandException commandException)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.RetryableFailed,
                commandException.StatusCode.ToString(),
                message);
        }

        return EmailSendResult.Failed(
            OutboundEmailAttemptStatus.RetryableFailed,
            exception.GetType().Name,
            message);
    }

    internal static MimeMessage BuildMimeMessage(
        OutboundEmailMessage message,
        string sendingEmailAddress,
        IReadOnlyList<string> recipients)
    {
        var emailToSend = new MimeMessage
        {
            Subject = message.Subject,
            Body = new TextPart(message.IsBodyHtml ? TextFormat.Html : TextFormat.Plain)
            {
                Text = message.Body
            }
        };

        emailToSend.From.Add(MailboxAddress.Parse(sendingEmailAddress));
        emailToSend.To.AddRange(recipients.Select(MailboxAddress.Parse));

        return emailToSend;
    }

    private IReadOnlyList<string> EffectiveRecipients(IReadOnlyList<string> recipients)
    {
        if (!configuration.IsProduction && !string.IsNullOrWhiteSpace(configuration.NonProductionSafeRecipient))
        {
            return [configuration.NonProductionSafeRecipient];
        }

        return recipients;
    }

    private static SecureSocketOptions ResolveSocketOptions(string mode)
    {
        if (mode.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return SecureSocketOptions.None;
        }

        if (mode.Equals("Ssl", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("SslOnConnect", StringComparison.OrdinalIgnoreCase))
        {
            return SecureSocketOptions.SslOnConnect;
        }

        if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return SecureSocketOptions.Auto;
        }

        if (mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Tls", StringComparison.OrdinalIgnoreCase))
        {
            return SecureSocketOptions.StartTls;
        }

        throw new InvalidOperationException($"Unsupported mailSecurityMode '{mode}'.");
    }

    internal static bool IsHandledSmtpException(Exception exception)
    {
        return exception is AuthenticationException
            or SmtpCommandException
            or SmtpProtocolException
            or InvalidOperationException
            or FormatException
            or IOException
            or OperationCanceledException
            or TimeoutException;
    }

    internal static TimeSpan GetRetryDelay(int failedAttempt)
    {
        var exponentialDelay = RetryBaseDelayMilliseconds * (1 << (failedAttempt - 1));
        var jitter = Random.Shared.Next(0, RetryBaseDelayMilliseconds + 1);
        return TimeSpan.FromMilliseconds(exponentialDelay + jitter);
    }
}
