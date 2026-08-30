using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailDeliveryOrchestrator(
    IEmailSender emailSender,
    IClock clock,
    ILogger<EmailDeliveryOrchestrator>? logger = null)
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

        logger?.LogInformation(
            "[EmailTrace 09] Email delivery orchestration started. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, MessageCount: {MessageCount}, PriorAttemptCount: {PriorAttemptCount}",
            submission.CorrelationId,
            submission.Id,
            messages.Count,
            attempts.Count);

        foreach (var message in messages)
        {
            if (attempts.Any(attempt => attempt.MessageKey == message.MessageKey
                    && attempt.Status == OutboundEmailAttemptStatus.Succeeded))
            {
                logger?.LogInformation(
                    "[EmailTrace 10 SKIPPED] Previously delivered email message skipped. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, MessageType: {MessageType}",
                    submission.CorrelationId,
                    submission.Id,
                    message.MessageType);
                continue;
            }

            logger?.LogInformation(
                "[EmailTrace 10] Email delivery attempt starting. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, MessageType: {MessageType}, RecipientCount: {RecipientCount}",
                submission.CorrelationId,
                submission.Id,
                message.MessageType,
                message.Recipients.Count);

            EmailSendResult result;
            if (message.Recipients.Count == 0)
            {
                result = EmailSendResult.Failed(
                    OutboundEmailAttemptStatus.TerminalFailed,
                    "NoRecipients",
                    "Outbound message has no recipients.");
            }
            else
            {
                result = await SendSafelyAsync(message, cancellationToken);
            }

            attempts.Add(ToAttempt(message, result));
            logger?.LogInformation(
                "[EmailTrace 16] Email delivery attempt recorded. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, MessageType: {MessageType}, AttemptStatus: {AttemptStatus}, ProviderCode: {ProviderCode}",
                submission.CorrelationId,
                submission.Id,
                message.MessageType,
                result.Status,
                result.ProviderCode);
            LogDeliveryAttemptFailure(submission, message, result);

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

        NormalizedInterestFormSubmission delivered;
        if (allRequiredMessagesSucceeded)
        {
            delivered = submission with
            {
                EmailDeliveryAttempts = attempts,
                EmailDeliveryStatus = EmailDeliveryStatus.Sent,
                SentOnUtc = clock.UtcNow,
                NextEmailAttemptOnUtc = null,
                Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        else
        {
            delivered = submission with
            {
                EmailDeliveryAttempts = attempts,
                EmailDeliveryStatus = hadTerminalFailure && !hadRetryableFailure
                    ? EmailDeliveryStatus.TerminalFailed
                    : EmailDeliveryStatus.RetryPending,
                SentOnUtc = null,
                NextEmailAttemptOnUtc = clock.UtcNow.AddDays(1),
                Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        logger?.LogInformation(
            "[EmailTrace 17] Email delivery status finalized. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}, AttemptCount: {AttemptCount}, ErrorCount: {ErrorCount}",
            submission.CorrelationId,
            submission.Id,
            delivered.EmailDeliveryStatus,
            delivered.EmailDeliveryAttempts.Count,
            delivered.Errors.Count);

        if (!allRequiredMessagesSucceeded)
        {
            var unresolvedMessages = messages
                .Where(message => !attempts.Any(attempt => attempt.MessageKey == message.MessageKey
                    && attempt.Status == OutboundEmailAttemptStatus.Succeeded))
                .Select(message =>
                {
                    var latestAttempt = attempts.LastOrDefault(attempt => attempt.MessageKey == message.MessageKey);
                    return latestAttempt is null
                        ? $"{message.MessageType}:NotAttempted"
                        : $"{message.MessageType}:{latestAttempt.Status}:{latestAttempt.ProviderCode}";
                })
                .ToList();

            logger?.LogError(
                "[EmailDeliveryFinalFailure] Email delivery run ended without all required messages succeeding. CosmosSubmissionId: {CosmosSubmissionId}, CorrelationId: {CorrelationId}, DeliveryStatus: {DeliveryStatus}, UnresolvedMessageCount: {UnresolvedMessageCount}, UnresolvedMessages: {UnresolvedMessages}",
                submission.Id,
                submission.CorrelationId,
                delivered.EmailDeliveryStatus,
                unresolvedMessages.Count,
                string.Join(", ", unresolvedMessages));
        }

        return delivered;
    }

    private void LogDeliveryAttemptFailure(
        NormalizedInterestFormSubmission submission,
        OutboundEmailMessage message,
        EmailSendResult result)
    {
        if (result.Status == OutboundEmailAttemptStatus.Succeeded)
        {
            return;
        }

        logger?.LogWarning(
            "[EmailDeliveryAttemptFailure] Outbound email message attempt failed. CosmosSubmissionId: {CosmosSubmissionId}, CorrelationId: {CorrelationId}, MessageType: {MessageType}, AttemptStatus: {AttemptStatus}, ProviderCode: {ProviderCode}, ProviderResponse: {ProviderResponse}",
            submission.Id,
            submission.CorrelationId,
            message.MessageType,
            result.Status,
            result.ProviderCode,
            result.ProviderResponse);
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
