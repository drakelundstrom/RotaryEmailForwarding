using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Retry;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class EmailRetryFunction(
    EmailRetryService retryService,
    ILogger<EmailRetryFunction> logger)
{
    [Function("RetryUnsentSubmissions")]
    public async Task Run(
        [TimerTrigger("0 0 8 * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var result = await retryService.RetryAsync(cancellationToken);
        const string message = "Email retry completed. Attempted: {Attempted}, Sent: {Sent}, RetryPending: {RetryPending}, TerminalFailed: {TerminalFailed}, RecipientUnitsAttempted: {RecipientUnitsAttempted}, StoppedForRecipientBudget: {StoppedForRecipientBudget}, StoppedForQuota: {StoppedForQuota}";
        var arguments = new object[]
        {
            result.Attempted,
            result.Sent,
            result.RetryPending,
            result.TerminalFailed,
            result.RecipientUnitsAttempted,
            result.StoppedForRecipientBudget,
            result.StoppedForQuota
        };

        if (result.RetryPending > 0
            || result.TerminalFailed > 0
            || result.StoppedForRecipientBudget
            || result.StoppedForQuota)
        {
            logger.LogError(message, arguments);
        }
        else
        {
            logger.LogInformation(message, arguments);
        }
    }
}
