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
        [TimerTrigger("0 0 3 * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var result = await retryService.RetryAsync(cancellationToken);
        logger.LogInformation(
            "Email retry completed. Attempted: {Attempted}, Sent: {Sent}, RetryPending: {RetryPending}, TerminalFailed: {TerminalFailed}, StoppedForQuota: {StoppedForQuota}",
            result.Attempted,
            result.Sent,
            result.RetryPending,
            result.TerminalFailed,
            result.StoppedForQuota);
    }
}
