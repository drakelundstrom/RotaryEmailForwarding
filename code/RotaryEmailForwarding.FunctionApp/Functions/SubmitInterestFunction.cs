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
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "interest-form-entry")] HttpRequestData request)
    {
        // The webhook caller has a deliberately short timeout. Once Azure Functions has
        // accepted the request, finish the durable submission workflow even if the caller
        // disconnects before SMTP delivery completes.
        var correlationId = GetCorrelationId(request);
        var rawBody = await new StreamReader(request.Body).ReadToEndAsync(CancellationToken.None);

        logger.LogInformation(
            "[EmailTrace 01] Interest form request received. CorrelationId: {CorrelationId}, RequestBodyLength: {RequestBodyLength}",
            correlationId,
            rawBody.Length);

        if (rawBody.Length > configuration.MaxRequestBodyBytes)
        {
            logger.LogError(
                "[EmailTrace 01 FAILED] Rejected oversized interest form submission. CorrelationId: {CorrelationId}, RequestBodyLength: {RequestBodyLength}, MaxRequestBodyBytes: {MaxRequestBodyBytes}",
                correlationId,
                rawBody.Length,
                configuration.MaxRequestBodyBytes);
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
                CancellationToken.None);

            logger.LogInformation(
                "[EmailTrace 02] Raw interest form request stored. CorrelationId: {CorrelationId}",
                correlationId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[EmailTrace 02 FAILED] Failed to store raw request log. CorrelationId: {CorrelationId}", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"submission unable to be logged to database: {exception.Message}",
                rawBody);
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
            logger.LogError(exception, "[EmailTrace 03 FAILED] Rejected malformed interest form submission payload. CorrelationId: {CorrelationId}", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"Failure to process submission or send email: {exception.Message}",
                rawBody);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.BadRequest,
                "The request body must be valid JSON.",
                correlationId);
        }

        if (submissionRequest is null)
        {
            logger.LogError(
                "[EmailTrace 03 FAILED] Rejected null interest form submission payload. CorrelationId: {CorrelationId}",
                correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                "Failure to process submission or send email: request body deserialized to null.",
                rawBody);
            return await CreateErrorResponse(
                request,
                HttpStatusCode.BadRequest,
                "The request body is required.",
                correlationId);
        }

        LogUnhandledFields(submissionRequest, correlationId);

        logger.LogInformation(
            "[EmailTrace 03] Interest form request parsed. CorrelationId: {CorrelationId}, HasSubmissionType: {HasSubmissionType}, HasCountry: {HasCountry}, HasZipcode: {HasZipcode}, HasContactEmail: {HasContactEmail}",
            correlationId,
            !string.IsNullOrWhiteSpace(submissionRequest.SubmissionType),
            !string.IsNullOrWhiteSpace(submissionRequest.CountryOfResidence),
            !string.IsNullOrWhiteSpace(submissionRequest.Zipcode),
            !string.IsNullOrWhiteSpace(submissionRequest.ContactEmail));

        SubmissionWorkflowResult result;
        try
        {
            result = await workflow.ProcessAsync(submissionRequest, correlationId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[EmailTrace FAILED] Interest form workflow threw an exception. CorrelationId: {CorrelationId}. The last completed numbered step identifies the failing operation.", correlationId);
            await SendOperatorFailureAsync(
                correlationId,
                $"Failure to send to database or process submission: {exception.Message}",
                rawBody);
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

        logger.LogInformation(
            "[EmailTrace 20] HTTP response ready. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}, DeliveryAttemptCount: {DeliveryAttemptCount}, ErrorCount: {ErrorCount}, HttpStatusCode: {HttpStatusCode}",
            correlationId,
            result.Submission.Id,
            result.Submission.EmailDeliveryStatus,
            result.Submission.EmailDeliveryAttempts.Count,
            result.Submission.Errors.Count,
            (int)statusCode);

        if (result.Submission.Errors.Count > 0)
        {
            logger.LogWarning(
                "Interest form submission completed with warnings. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}, Warnings: {Warnings}",
                correlationId,
                result.Submission.Id,
                result.Submission.EmailDeliveryStatus,
                string.Join("; ", result.Submission.Errors));
        }

        var response = request.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new
        {
            correlationId,
            result.Submission.Id,
            result.Submission.ReceivedOnUtc,
            result.Submission.SentOnUtc,
            EmailDeliveryStatus = result.Submission.EmailDeliveryStatus.ToString(),
            result.Submission.Errors
        }, CancellationToken.None);

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

        logger.LogError(
            "Interest form submission included unhandled fields. CorrelationId: {CorrelationId}, Fields: {UnhandledFields}",
            correlationId,
            string.Join(", ", request.UnhandledFields.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)));
    }

    private async Task SendOperatorFailureAsync(
        string correlationId,
        string failureSummary,
        string rawSubmissionJson)
    {
        try
        {
            logger.LogInformation(
                "Sending operator failure email. CorrelationId: {CorrelationId}",
                correlationId);

            var message = templateService.BuildOperatorFailureMessage(correlationId, failureSummary, rawSubmissionJson);
            var result = await emailSender.SendAsync(message, CancellationToken.None);
            logger.LogInformation(
                "Operator failure email attempt completed. CorrelationId: {CorrelationId}, AttemptStatus: {AttemptStatus}, ProviderCode: {ProviderCode}",
                correlationId,
                result.Status,
                result.ProviderCode);
            if (result.Status != OutboundEmailAttemptStatus.Succeeded)
            {
                logger.LogError(
                    "Failed to send operator failure email. CorrelationId: {CorrelationId}, ProviderCode: {ProviderCode}, ProviderResponse: {ProviderResponse}",
                    correlationId,
                    result.ProviderCode,
                    result.ProviderResponse);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to send operator failure email. CorrelationId: {CorrelationId}", correlationId);
        }
    }
}
