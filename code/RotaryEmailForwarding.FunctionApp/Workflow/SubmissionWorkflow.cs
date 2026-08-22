using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Workflow;

public sealed record SubmissionWorkflowResult
{
    public required NormalizedInterestFormSubmission Submission { get; init; }

    public required bool WasStored { get; init; }

    public required bool DeliveryCompleted { get; init; }
}

public sealed class SubmissionWorkflow(
    IApplicationRepository repository,
    SubmissionRoutingService routingService,
    EmailTemplateService templateService,
    EmailDeliveryOrchestrator deliveryOrchestrator,
    IClock clock,
    ILogger<SubmissionWorkflow>? logger = null)
{
    public async Task<SubmissionWorkflowResult> ProcessAsync(
        InterestFormSubmissionRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var submission = SubmissionNormalizer.Normalize(request, clock.UtcNow) with
        {
            CorrelationId = correlationId
        };

        logger?.LogInformation(
            "[EmailTrace 04] Submission normalized. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, HasCountry: {HasCountry}, HasZipcode: {HasZipcode}",
            correlationId,
            submission.Id,
            !string.IsNullOrWhiteSpace(submission.CountryOfResidence),
            !string.IsNullOrWhiteSpace(submission.Zipcode));

        await repository.InsertSubmissionAsync(submission, cancellationToken);

        logger?.LogInformation(
            "[EmailTrace 05] Initial submission persisted. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}",
            correlationId,
            submission.Id,
            submission.EmailDeliveryStatus);

        var route = await routingService.RouteAsync(submission, cancellationToken);
        logger?.LogInformation(
            "[EmailTrace 06] Submission routing completed. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DistrictRouteCount: {DistrictRouteCount}, HasCountryRoute: {HasCountryRoute}, RoutingErrorCount: {RoutingErrorCount}",
            correlationId,
            submission.Id,
            route.DistrictContacts.Count,
            route.CountryContact is not null,
            route.Errors.Count);
        var routeErrors = route.Errors.Count == 0
            ? submission.Errors
            : submission.Errors.Concat(route.Errors).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        submission = submission with
        {
            Errors = routeErrors,
            RoutedDistricts = route.DistrictContacts.Select(contact => contact.District).ToList(),
            RoutedCountry = route.CountryContact?.Country
        };

        var message = templateService.BuildMessage(submission, route);
        logger?.LogInformation(
            "[EmailTrace 07] Outbound message prepared. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, MessageType: {MessageType}, RecipientCount: {RecipientCount}, IsBodyHtml: {IsBodyHtml}",
            correlationId,
            submission.Id,
            message.MessageType,
            message.Recipients.Count,
            message.IsBodyHtml);
        if (EmailTemplateService.BuildInterestedPartyRecipients(submission).Count == 0)
        {
            submission = submission with
            {
                Errors = submission.Errors
                    .Append("Interested party email missing or unusable; they were not included on the outbound email")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        // Persist the complete outbound-email state before making the external call. If the
        // process stops during delivery, the scheduled retry can safely recover this record.
        await repository.UpdateSubmissionAsync(submission, cancellationToken);

        logger?.LogInformation(
            "[EmailTrace 08] Pre-delivery submission state persisted. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, ErrorCount: {ErrorCount}",
            correlationId,
            submission.Id,
            submission.Errors.Count);

        var delivered = await deliveryOrchestrator.DeliverAsync(submission, [message], cancellationToken);
        logger?.LogInformation(
            "[EmailTrace 18] Email delivery orchestration returned. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}, DeliveryAttemptCount: {DeliveryAttemptCount}, ErrorCount: {ErrorCount}",
            correlationId,
            submission.Id,
            delivered.EmailDeliveryStatus,
            delivered.EmailDeliveryAttempts.Count,
            delivered.Errors.Count);

        await repository.UpdateSubmissionAsync(delivered, cancellationToken);

        logger?.LogInformation(
            "[EmailTrace 19] Final submission state persisted. CorrelationId: {CorrelationId}, SubmissionId: {SubmissionId}, DeliveryStatus: {DeliveryStatus}",
            correlationId,
            submission.Id,
            delivered.EmailDeliveryStatus);

        return new SubmissionWorkflowResult
        {
            Submission = delivered,
            WasStored = true,
            DeliveryCompleted = delivered.EmailDeliveryStatus == EmailDeliveryStatus.Sent
        };
    }
}
