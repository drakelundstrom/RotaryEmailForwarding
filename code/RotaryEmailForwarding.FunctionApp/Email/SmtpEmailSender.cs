using System.Net;
using System.Net.Mail;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class SmtpEmailSender(AppConfiguration configuration) : IEmailSender
{
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

        try
        {
            using var smtpClient = new SmtpClient(configuration.MailHost, configuration.MailPort)
            {
                EnableSsl = IsSslEnabled(configuration.MailSecurityMode),
                Credentials = new NetworkCredential(configuration.SendingEmailAddress, configuration.SendingEmailPassword)
            };

            using var mailMessage = new MailMessage
            {
                From = new MailAddress(configuration.SendingEmailAddress),
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = message.IsBodyHtml
            };

            foreach (var recipient in EffectiveRecipients(message.Recipients))
            {
                mailMessage.To.Add(recipient);
            }

            await smtpClient.SendMailAsync(mailMessage, cancellationToken);
            return EmailSendResult.Success("SmtpAccepted", "Message accepted by SMTP provider.");
        }
        catch (SmtpException exception)
        {
            return Classify(exception);
        }
        catch (InvalidOperationException exception)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "InvalidSmtpOperation",
                exception.Message);
        }
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

        if (exception is SmtpException smtpException
            && smtpException.StatusCode is SmtpStatusCode.TransactionFailed
                or SmtpStatusCode.ServiceNotAvailable
                or SmtpStatusCode.MailboxBusy
                or SmtpStatusCode.MailboxNameNotAllowed)
        {
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.RetryableFailed,
                smtpException.StatusCode.ToString(),
                message);
        }

        return EmailSendResult.Failed(
            OutboundEmailAttemptStatus.RetryableFailed,
            exception.GetType().Name,
            message);
    }

    private IReadOnlyList<string> EffectiveRecipients(IReadOnlyList<string> recipients)
    {
        if (!configuration.IsProduction && !string.IsNullOrWhiteSpace(configuration.NonProductionSafeRecipient))
        {
            return [configuration.NonProductionSafeRecipient];
        }

        return recipients;
    }

    private static bool IsSslEnabled(string mode)
    {
        return mode.Equals("StartTls", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Ssl", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("Tls", StringComparison.OrdinalIgnoreCase);
    }
}
