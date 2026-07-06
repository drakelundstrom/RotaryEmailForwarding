using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailDeliveryOrchestrator(IEmailSender emailSender, IClock clock)
{
    public async Task<NormalizedInterestFormSubmission> DeliverAsync(
        NormalizedInterestFormSubmission submission,
        IReadOnlyList<OutboundEmailMessage> messages,
        CancellationToken cancellationToken)
    {
        var attempts = submission.EmailDeliveryAttempts.ToList();
        var errors = submission.Errors.ToList();
        var hadRetryableFailure = false;
        var hadTerminalFailure = false;
        var hadQuotaFailure = false;

        foreach (var message in messages)
        {
            if (attempts.Any(attempt => attempt.MessageKey == message.MessageKey
                    && attempt.Status == OutboundEmailAttemptStatus.Succeeded))
            {
                continue;
            }

            if (message.Recipients.Count == 0)
            {
                attempts.Add(ToAttempt(
                    message,
                    EmailSendResult.Failed(OutboundEmailAttemptStatus.TerminalFailed, "NoRecipients", "Outbound message has no recipients.")));
                errors.Add($"No recipients for {message.MessageType}");
                hadTerminalFailure = true;
                continue;
            }

            var result = await SendSafelyAsync(message, cancellationToken);
            attempts.Add(ToAttempt(message, result));

            switch (result.Status)
            {
                case OutboundEmailAttemptStatus.Succeeded:
                    break;
                case OutboundEmailAttemptStatus.QuotaExceeded:
                    hadQuotaFailure = true;
                    hadRetryableFailure = true;
                    errors.Add($"Quota exceeded for {message.MessageType}: {result.ProviderResponse}");
                    break;
                case OutboundEmailAttemptStatus.RetryableFailed:
                    hadRetryableFailure = true;
                    errors.Add($"Retryable email failure for {message.MessageType}: {result.ProviderResponse}");
                    break;
                default:
                    hadTerminalFailure = true;
                    errors.Add($"Terminal email failure for {message.MessageType}: {result.ProviderResponse}");
                    break;
            }

            if (hadQuotaFailure)
            {
                break;
            }
        }

        var requiredMessageKeys = messages.Select(message => message.MessageKey).ToHashSet(StringComparer.Ordinal);
        var allRequiredMessagesSucceeded = requiredMessageKeys.All(key =>
            attempts.Any(attempt => attempt.MessageKey == key && attempt.Status == OutboundEmailAttemptStatus.Succeeded));

        if (allRequiredMessagesSucceeded)
        {
            return submission with
            {
                EmailDeliveryAttempts = attempts,
                EmailDeliveryStatus = EmailDeliveryStatus.Sent,
                SentOnUtc = clock.UtcNow,
                NextEmailAttemptOnUtc = null,
                Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        return submission with
        {
            EmailDeliveryAttempts = attempts,
            EmailDeliveryStatus = hadTerminalFailure && !hadRetryableFailure
                ? EmailDeliveryStatus.TerminalFailed
                : EmailDeliveryStatus.RetryPending,
            SentOnUtc = null,
            NextEmailAttemptOnUtc = hadRetryableFailure ? clock.UtcNow.AddDays(1) : null,
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private OutboundEmailAttempt ToAttempt(OutboundEmailMessage message, EmailSendResult result)
    {
        return new OutboundEmailAttempt
        {
            MessageKey = message.MessageKey,
            MessageType = message.MessageType,
            Recipients = message.Recipients,
            AttemptedOnUtc = clock.UtcNow,
            Status = result.Status,
            ProviderCode = result.ProviderCode,
            ProviderResponse = result.ProviderResponse
        };
    }

    private async Task<EmailSendResult> SendSafelyAsync(
        OutboundEmailMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await emailSender.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var status = exception is ArgumentException or FormatException or InvalidOperationException
                ? OutboundEmailAttemptStatus.TerminalFailed
                : OutboundEmailAttemptStatus.RetryableFailed;

            return EmailSendResult.Failed(status, exception.GetType().Name, exception.Message);
        }
    }
}
