using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Authorization;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Workflow;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class CatchUpSubmissionsFunction(
    AdminAuthorizationService authorizationService,
    CatchUpSubmissionWorkflow workflow,
    AppConfiguration configuration,
    ILogger<CatchUpSubmissionsFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("CatchUpSubmissions")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "catch-up-interest-form-entries")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return request.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var rawBody = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);
        if (rawBody.Length > configuration.MaxRequestBodyBytes)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "The request body is too large.");
        }

        IReadOnlyList<CatchUpSubmissionRequest>? submissions;
        try
        {
            submissions = JsonSerializer.Deserialize<List<CatchUpSubmissionRequest>>(rawBody, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed catch-up request.");
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "The request body must be a valid JSON array.");
        }

        if (submissions is null || submissions.Count == 0)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "At least one submission is required.");
        }

        if (submissions.Count > CatchUpSubmissionWorkflow.MaximumBatchSize)
        {
            return await ErrorAsync(
                request,
                HttpStatusCode.BadRequest,
                $"A batch may contain at most {CatchUpSubmissionWorkflow.MaximumBatchSize} submissions.");
        }

        if (submissions.Any(item => item is null
                || item.Submission is null
                || item.OriginalProcessedOnUtc == default))
        {
            return await ErrorAsync(
                request,
                HttpStatusCode.BadRequest,
                "Every array item requires originalProcessedOnUtc and submission.");
        }

        var result = await workflow.ProcessAsync(submissions, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            result.Sent,
            result.AlreadySent,
            result.NotFound,
            result.Ambiguous,
            result.Failed,
            items = result.Items.Select(item => new
            {
                item.Index,
                status = item.Status.ToString(),
                item.SubmissionId,
                item.Message
            })
        }, cancellationToken);
        return response;
    }

    private static async Task<HttpResponseData> ErrorAsync(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string message)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }
}
