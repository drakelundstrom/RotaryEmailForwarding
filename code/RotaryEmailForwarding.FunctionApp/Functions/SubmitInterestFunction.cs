using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class SubmitInterestFunction(ILogger<SubmitInterestFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("SubmitInterest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "interest-form-submissions")] HttpRequestData request)
    {
        InterestFormSubmissionRequest? submissionRequest;

        try
        {
            submissionRequest = await JsonSerializer.DeserializeAsync<InterestFormSubmissionRequest>(
                request.Body,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed interest form submission payload.");
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, "The request body must be valid JSON.");
        }

        if (submissionRequest is null)
        {
            return await CreateErrorResponse(request, HttpStatusCode.BadRequest, "The request body is required.");
        }

        var normalizedSubmission = SubmissionNormalizer.Normalize(submissionRequest, DateTimeOffset.UtcNow);
        var response = request.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(normalizedSubmission);

        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string message)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });

        return response;
    }
}
