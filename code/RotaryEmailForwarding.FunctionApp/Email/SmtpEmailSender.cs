using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Text;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using System.Text;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class SmtpEmailSender(
    AppConfiguration configuration,
    ILogger<SmtpEmailSender>? logger = null) : IEmailSender
{
    private const int MaxSendAttempts = 3;
    private const int RetryBaseDelayMilliseconds = 500;
    private const int SmtpTimeoutMilliseconds = 30_000;

    public async Task<EmailSendResult> SendAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
    {
        logger?.LogInformation(
            "[EmailTrace 11] SMTP preflight started. MessageType: {MessageType}, RecipientCount: {RecipientCount}, AppEnvironment: {AppEnvironment}, IsProduction: {IsProduction}, MailHost: {MailHost}, MailPort: {MailPort}, MailSecurityMode: {MailSecurityMode}, HasSendingAddress: {HasSendingAddress}, HasSmtpPassword: {HasSmtpPassword}, HasNonProductionSafeRecipient: {HasNonProductionSafeRecipient}, AllowUnsafeNonProductionEmail: {AllowUnsafeNonProductionEmail}",
            message.MessageType,
            message.Recipients.Count,
            configuration.AppEnvironment,
            configuration.IsProduction,
            configuration.MailHost,
            configuration.MailPort,
            configuration.MailSecurityMode,
            !string.IsNullOrWhiteSpace(configuration.SendingEmailAddress),
            !string.IsNullOrWhiteSpace(configuration.SendingEmailPassword),
            !string.IsNullOrWhiteSpace(configuration.NonProductionSafeRecipient),
            configuration.AllowUnsafeNonProductionEmail);

        if (message.Recipients.Count == 0)
        {
            logger?.LogWarning(
                "[EmailTrace 11 FAILED] SMTP preflight failed. MessageType: {MessageType}, ReasonCode: {ReasonCode}",
                message.MessageType,
                "NoRecipients");
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "NoRecipients",
                "Outbound message has no recipients.");
        }

        if (!EmailAddressUtility.IsUsable(configuration.SendingEmailAddress))
        {
            logger?.LogWarning(
                "[EmailTrace 11 FAILED] SMTP preflight failed. MessageType: {MessageType}, ReasonCode: {ReasonCode}",
                message.MessageType,
                "MissingSendingEmailAddress");
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "MissingSendingEmailAddress",
                "sendingEmailAddress is required.");
        }

        if (!configuration.IsProduction
            && string.IsNullOrWhiteSpace(configuration.NonProductionSafeRecipient)
            && !configuration.AllowUnsafeNonProductionEmail)
        {
            logger?.LogWarning(
                "[EmailTrace 11 FAILED] SMTP preflight failed. MessageType: {MessageType}, ReasonCode: {ReasonCode}",
                message.MessageType,
                "UnsafeNonProductionEmailBlocked");
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "UnsafeNonProductionEmailBlocked",
                "Non-production email requires nonProductionSafeRecipient or allowUnsafeNonProductionEmail=true.");
        }

        if (string.IsNullOrWhiteSpace(configuration.SendingEmailPassword))
        {
            logger?.LogWarning(
                "[EmailTrace 11 FAILED] SMTP preflight failed. MessageType: {MessageType}, ReasonCode: {ReasonCode}",
                message.MessageType,
                "MissingSmtpPassword");
            return EmailSendResult.Failed(
                OutboundEmailAttemptStatus.TerminalFailed,
                "MissingSmtpPassword",
                "sendingEmailPassword is required.");
        }

        for (var attempt = 1; attempt <= MaxSendAttempts; attempt++)
        {
            var lastCompletedStep = 11;
            try
            {
                logger?.LogInformation(
                    "[EmailTrace 12] SMTP attempt starting. MessageType: {MessageType}, Attempt: {Attempt}, MaximumAttempts: {MaximumAttempts}, EffectiveRecipientCount: {EffectiveRecipientCount}",
                    message.MessageType,
                    attempt,
                    MaxSendAttempts,
                    EffectiveRecipients(message.Recipients).Count);
                lastCompletedStep = 12;

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
                logger?.LogInformation(
                    "[EmailTrace 13] SMTP connection established. MessageType: {MessageType}, Attempt: {Attempt}, IsConnected: {IsConnected}, IsSecure: {IsSecure}",
                    message.MessageType,
                    attempt,
                    emailClient.IsConnected,
                    emailClient.IsSecure);
                lastCompletedStep = 13;

                var smtpPassword = configuration.SendingEmailPassword ?? string.Empty;
                logger?.LogCritical(
                    "[TEMPORARY SENSITIVE DEBUG] SMTP password loaded by worker. WorkerInstanceId: {WorkerInstanceId}, Password: [{Password}], PasswordLength: {PasswordLength}, PasswordBase64: {PasswordBase64}, IsUnresolvedKeyVaultReference: {IsUnresolvedKeyVaultReference}",
                    Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? Environment.MachineName,
                    smtpPassword,
                    smtpPassword.Length,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(smtpPassword)),
                    smtpPassword.StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase));

                await emailClient.AuthenticateAsync(
                    configuration.SendingEmailAddress,
                    smtpPassword,
                    cancellationToken);
                logger?.LogInformation(
                    "[EmailTrace 14] SMTP authentication completed. MessageType: {MessageType}, Attempt: {Attempt}, IsAuthenticated: {IsAuthenticated}",
                    message.MessageType,
                    attempt,
                    emailClient.IsAuthenticated);
                lastCompletedStep = 14;

                await emailClient.SendAsync(emailToSend, cancellationToken);
                logger?.LogInformation(
                    "[EmailTrace 15] SMTP provider accepted message. MessageType: {MessageType}, Attempt: {Attempt}, RecipientCount: {RecipientCount}",
                    message.MessageType,
                    attempt,
                    EffectiveRecipients(message.Recipients).Count);
                lastCompletedStep = 15;

                // SendAsync completed, so the provider accepted the message. A failure while
                // politely closing the connection must not cause the message to be sent twice.
                try
                {
                    await emailClient.DisconnectAsync(true, cancellationToken);
                }
                catch (Exception exception) when (IsHandledSmtpException(exception))
                {
                    logger?.LogWarning(
                        "[EmailTrace 15 WARNING] SMTP disconnect failed after provider acceptance. MessageType: {MessageType}, Attempt: {Attempt}, ExceptionType: {ExceptionType}",
                        message.MessageType,
                        attempt,
                        exception.GetType().Name);
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
                logger?.LogWarning(
                    "[EmailTrace SMTP FAILED] SMTP attempt failed after step {LastCompletedStep}. MessageType: {MessageType}, Attempt: {Attempt}, MaximumAttempts: {MaximumAttempts}, ExceptionType: {ExceptionType}, AttemptStatus: {AttemptStatus}, ProviderCode: {ProviderCode}",
                    lastCompletedStep,
                    message.MessageType,
                    attempt,
                    MaxSendAttempts,
                    exception.GetType().Name,
                    result.Status,
                    result.ProviderCode);
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
