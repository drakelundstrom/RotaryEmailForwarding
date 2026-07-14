using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Storage;
using RotaryEmailForwarding.FunctionApp.Workflow;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class SubmitInterestFunction(
    IApplicationRepository repository,
    SubmissionWorkflow workflow,
    EmailTemplateService templateService,
    IEmailSender emailSender,
    AppConfiguration configuration,
    ILogger<SubmitInterestFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("SubmitInterest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "interest-form-entry")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var correlationId = GetCorrelationId(request);
        var rawBody = await new StreamReader(request.Body).ReadToEndAsync(cancellationToken);

        if (rawBody.Length > configuration.MaxRequestBodyBytes)
        {
            return await CreateErrorResponse(
                request,
                HttpStatusCode.BadRequest,
                "The request body is too large.",
                correlationId);
        }

        try
        {
            await repository.StoreRawRequestAsync(
                new RequestBodyLog
                {
                    CorrelationId = correlationId,
                    RequestBody = rawBody,
                    ReceivedOnUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to store raw request log. CorrelationId: {CorrelationId}", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"submission unable to be logged to database: {exception.Message}",
                rawBody,
                cancellationToken);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.InternalServerError,
                "The submission could not be stored.",
                correlationId);
        }

        InterestFormSubmissionRequest? submissionRequest;

        try
        {
            submissionRequest = JsonSerializer.Deserialize<InterestFormSubmissionRequest>(rawBody, JsonOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Rejected malformed interest form submission payload. CorrelationId: {CorrelationId}", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"Failure to process submission or send email: {exception.Message}",
                rawBody,
                cancellationToken);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.BadRequest,
                "The request body must be valid JSON.",
                correlationId);
        }

        if (submissionRequest is null)
        {
            await SendOperatorFailureAsync(
                correlationId,
                "Failure to process submission or send email: request body deserialized to null.",
                rawBody,
                cancellationToken);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.BadRequest,
                "The request body is required.",
                correlationId);
        }

        LogUnhandledFields(submissionRequest, correlationId);

        SubmissionWorkflowResult result;
        try
        {
            result = await workflow.ProcessAsync(submissionRequest, correlationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to process interest form submission. CorrelationId: {CorrelationId}", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"Failure to send to database or process submission: {exception.Message}",
                rawBody,
                cancellationToken);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.InternalServerError,
                "The submission could not be processed.",
                correlationId);
        }

        var statusCode = result.Submission.EmailDeliveryStatus switch
        {
            EmailDeliveryStatus.Sent => HttpStatusCode.OK,
            EmailDeliveryStatus.RetryPending => HttpStatusCode.Accepted,
            _ => HttpStatusCode.InternalServerError
        };

        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new
        {
            correlationId,
            result.Submission.Id,
            result.Submission.ReceivedOnUtc,
            result.Submission.SentOnUtc,
            EmailDeliveryStatus = result.Submission.EmailDeliveryStatus.ToString(),
            result.Submission.Errors
        }, cancellationToken);

        return response;
    }

    private static async Task<HttpResponseData> CreateErrorResponse(
        HttpRequestData request,
        HttpStatusCode statusCode,
        string message,
        string correlationId)
    {
        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { correlationId, error = message });

        return response;
    }

    private static string GetCorrelationId(HttpRequestData request)
    {
        return request.Headers.TryGetValues("x-correlation-id", out var values)
            ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? Guid.NewGuid().ToString("D")
            : Guid.NewGuid().ToString("D");
    }

    private void LogUnhandledFields(InterestFormSubmissionRequest request, string correlationId)
    {
        if (request.UnhandledFields is null || request.UnhandledFields.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Interest form submission included unhandled fields. CorrelationId: {CorrelationId}, Fields: {UnhandledFields}",
            correlationId,
            string.Join(", ", request.UnhandledFields.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));
    }

    private async Task SendOperatorFailureAsync(
        string correlationId,
        string failureSummary,
        string rawSubmissionJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = templateService.BuildOperatorFailureMessage(correlationId, failureSummary, rawSubmissionJson);
            var result = await emailSender.SendAsync(message, cancellationToken);
            if (result.Status != OutboundEmailAttemptStatus.Succeeded)
            {
                logger.LogError(
                    "Failed to send operator failure email. CorrelationId: {CorrelationId}, ProviderCode: {ProviderCode}, ProviderResponse: {ProviderResponse}",
                    correlationId,
                    result.ProviderCode,
                    result.ProviderResponse);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to send operator failure email. CorrelationId: {CorrelationId}", correlationId);
        }
    }
}
