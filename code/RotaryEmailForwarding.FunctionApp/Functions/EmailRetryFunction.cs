using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Retry;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class EmailRetryFunction(
    EmailRetryService retryService,
    AppConfiguration configuration,
    ILogger<EmailRetryFunction> logger)
{
    [Function("RetryUnsentSubmissions")]
    public async Task Run(
        [TimerTrigger("0 0 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var retryTimeZone = RetryTimeZone.Resolve(configuration.EmailRetryTimeZone);
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, retryTimeZone);
        if (localNow.Hour != 3)
        {
            logger.LogInformation(
                "Skipping email retry because local retry hour has not arrived. LocalTime: {LocalTime}, TimeZone: {TimeZone}",
                localNow,
                retryTimeZone.Id);
            return;
        }

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
