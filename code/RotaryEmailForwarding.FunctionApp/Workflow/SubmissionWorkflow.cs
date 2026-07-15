using System.Text.Json;
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
    IClock clock)
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

        await repository.InsertSubmissionAsync(submission, cancellationToken);

        var route = await routingService.RouteAsync(submission, cancellationToken);
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

        var delivered = await deliveryOrchestrator.DeliverAsync(submission, [message], cancellationToken);
        await repository.UpdateSubmissionAsync(delivered, cancellationToken);

        return new SubmissionWorkflowResult
        {
            Submission = delivered,
            WasStored = true,
            DeliveryCompleted = delivered.EmailDeliveryStatus == EmailDeliveryStatus.Sent
        };
    }
}
