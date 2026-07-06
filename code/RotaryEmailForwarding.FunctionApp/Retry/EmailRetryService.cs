using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Retry;

public sealed record EmailRetryRunResult(int Attempted, int Sent, int RetryPending, int TerminalFailed, bool StoppedForQuota);

public sealed class EmailRetryService(
    IApplicationRepository repository,
    SubmissionRoutingService routingService,
    EmailTemplateService templateService,
    EmailDeliveryOrchestrator deliveryOrchestrator,
    IClock clock)
{
    public async Task<EmailRetryRunResult> RetryAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var previousDayStart = now.Date.AddDays(-1);
        var previousDayEnd = now.Date;

        var submissions = await repository.GetRetryableUnsentSubmissionsAsync(
            previousDayStart,
            previousDayEnd,
            now,
            100,
            cancellationToken);

        var attempted = 0;
        var sent = 0;
        var retryPending = 0;
        var terminalFailed = 0;
        var stoppedForQuota = false;

        foreach (var submission in submissions)
        {
            attempted++;
            var route = await routingService.RouteAsync(submission, cancellationToken);
            var messages = templateService.BuildMessages(submission, route, "{}");
            var delivered = await deliveryOrchestrator.DeliverAsync(submission, messages, cancellationToken);
            await repository.UpdateSubmissionAsync(delivered, cancellationToken);

            if (delivered.EmailDeliveryAttempts.Any(attempt => attempt.Status == OutboundEmailAttemptStatus.QuotaExceeded
                    && !submission.EmailDeliveryAttempts.Contains(attempt)))
            {
                stoppedForQuota = true;
            }

            switch (delivered.EmailDeliveryStatus)
            {
                case EmailDeliveryStatus.Sent:
                    sent++;
                    break;
                case EmailDeliveryStatus.TerminalFailed:
                    terminalFailed++;
                    break;
                default:
                    retryPending++;
                    break;
            }

            if (stoppedForQuota)
            {
                break;
            }
        }

        return new EmailRetryRunResult(attempted, sent, retryPending, terminalFailed, stoppedForQuota);
    }
}
