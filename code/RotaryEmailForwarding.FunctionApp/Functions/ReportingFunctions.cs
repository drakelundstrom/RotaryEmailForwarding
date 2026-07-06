using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RotaryEmailForwarding.FunctionApp.Authorization;
using RotaryEmailForwarding.FunctionApp.Reporting;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class ReportingFunctions(
    ReportingService reportingService,
    AdminAuthorizationService authorizationService)
{
    [Function("GenerateSubmissionsByMonth")]
    public async Task<HttpResponseData> GenerateSubmissionsByMonth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "generate-submissions-by-month")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return await ErrorAsync(request, HttpStatusCode.Unauthorized, "Admin authorization is required.");
        }

        var query = ParseQuery(request.Url.Query);
        if (!query.TryGetValue("start", out var startValue)
            || !query.TryGetValue("end", out var endValue)
            || !DateTimeOffset.TryParse(startValue, out var start)
            || !DateTimeOffset.TryParse(endValue, out var end))
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "Query parameters start and end are required dates.");
        }

        var buckets = await reportingService.GenerateSubmissionsByMonthAsync(start, end, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(buckets, cancellationToken);
        return response;
    }

    [Function("GetSubmissionsForDistrict")]
    public async Task<HttpResponseData> GetSubmissionsForDistrict(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "submissions/district/{districtName}")] HttpRequestData request,
        string districtName,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return await ErrorAsync(request, HttpStatusCode.Unauthorized, "Admin authorization is required.");
        }

        var since = DateTimeOffset.UtcNow.AddYears(-1);
        var submissions = await reportingService.GetSubmissionsByDistrictAsync(districtName, since, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(submissions, cancellationToken);
        return response;
    }

    private static async Task<HttpResponseData> ErrorAsync(HttpRequestData request, HttpStatusCode status, string message)
    {
        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        return query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }
}
