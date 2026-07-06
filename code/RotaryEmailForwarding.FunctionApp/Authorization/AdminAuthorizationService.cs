using Microsoft.Azure.Functions.Worker.Http;
using RotaryEmailForwarding.FunctionApp.Configuration;

namespace RotaryEmailForwarding.FunctionApp.Authorization;

public sealed class AdminAuthorizationService(AppConfiguration configuration)
{
    public bool IsAuthorized(HttpRequestData request)
    {
        if (string.IsNullOrWhiteSpace(configuration.AdminApiKey))
        {
            return false;
        }

        return request.Headers.TryGetValues("x-admin-api-key", out var values)
            && values.Any(value => string.Equals(value, configuration.AdminApiKey, StringComparison.Ordinal));
    }
}
