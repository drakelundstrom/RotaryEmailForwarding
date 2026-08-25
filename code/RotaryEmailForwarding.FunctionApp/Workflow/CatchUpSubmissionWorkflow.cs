using Microsoft.Extensions.Logging;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Workflow;

public sealed class CatchUpSubmissionWorkflow(
    IApplicationRepository repository,
    SubmissionRoutingService routingService,
    EmailTemplateService templateService,
    EmailDeliveryOrchestrator deliveryOrchestrator,
    ILogger<CatchUpSubmissionWorkflow>? logger = null)
{
    public const int MaximumBatchSize = 20;
    internal static readonly TimeSpan MatchDateTolerance = TimeSpan.FromHours(12);
    private const int MinimumCorroboratingFieldMatches = 2;

    public async Task<CatchUpBatchResult> ProcessAsync(
        IReadOnlyList<CatchUpSubmissionRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(requests.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requests.Count, MaximumBatchSize);

        var earliest = requests.Min(item => item.OriginalProcessedOnUtc) - MatchDateTolerance;
        var latest = requests.Max(item => item.OriginalProcessedOnUtc) + MatchDateTolerance;
        var storedSubmissions = await repository.GetSubmissionsByReceivedOnOrStorageTimestampRangeAsync(
            earliest,
            latest.AddTicks(1),
            cancellationToken);

        var results = new List<CatchUpSubmissionItemResult>(requests.Count);
        var processedSubmissionIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var normalizedRequest = SubmissionNormalizer.Normalize(
                request.Submission,
                request.OriginalProcessedOnUtc);
            var matches = storedSubmissions
                .Where(candidate => IsMatch(candidate, normalizedRequest, request.OriginalProcessedOnUtc))
                .ToList();

            if (matches.Count == 0)
            {
                results.Add(Result(index, CatchUpSubmissionStatus.NotFound, null,
                    "No existing submission matched the email, corroborating fields, and processing-date window."));
                continue;
            }

            if (matches.Count > 1)
            {
                results.Add(Result(index, CatchUpSubmissionStatus.Ambiguous, null,
                    $"{matches.Count} existing submissions matched; no record was changed and no email was sent."));
                continue;
            }

            var submission = matches[0];
            if (!processedSubmissionIds.Add(submission.Id))
            {
                results.Add(Result(index, CatchUpSubmissionStatus.AlreadySent, submission.Id,
                    "This existing submission was already handled earlier in the same batch."));
                continue;
            }

            if (submission.EmailDeliveryStatus == EmailDeliveryStatus.Sent || submission.SentOnUtc is not null)
            {
                results.Add(Result(index, CatchUpSubmissionStatus.AlreadySent, submission.Id,
                    "The existing submission is already marked as sent."));
                continue;
            }

            try
            {
                results.Add(await DeliverAsync(index, submission, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogError(
                    exception,
                    "Catch-up processing failed. BatchIndex: {BatchIndex}, SubmissionId: {SubmissionId}",
                    index,
                    submission.Id);
                results.Add(Result(index, CatchUpSubmissionStatus.Failed, submission.Id, exception.Message));
            }
        }

        return new CatchUpBatchResult { Items = results };
    }

    private async Task<CatchUpSubmissionItemResult> DeliverAsync(
        int index,
        NormalizedInterestFormSubmission submission,
        CancellationToken cancellationToken)
    {
        var route = await routingService.RouteAsync(submission, cancellationToken);
        var routed = submission with
        {
            Errors = submission.Errors
                .Concat(route.Errors)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            RoutedDistricts = route.DistrictContacts.Select(contact => contact.District).ToList(),
            RoutedCountry = route.CountryContact?.Country
        };
        var message = templateService.BuildCatchUpMessage(routed, route);

        if (EmailTemplateService.BuildInterestedPartyRecipients(routed).Count == 0)
        {
            routed = routed with
            {
                Errors = routed.Errors
                    .Append("Interested party email missing or unusable; they were not included on the outbound email")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        // Replace-only writes are deliberate: this workflow must never create a missing record.
        await repository.ReplaceSubmissionAsync(routed, cancellationToken);
        var delivered = await deliveryOrchestrator.DeliverAsync(routed, [message], cancellationToken);
        await repository.ReplaceSubmissionAsync(delivered, cancellationToken);

        var status = delivered.EmailDeliveryStatus == EmailDeliveryStatus.Sent
            ? CatchUpSubmissionStatus.Sent
            : CatchUpSubmissionStatus.Failed;
        return Result(
            index,
            status,
            delivered.Id,
            status == CatchUpSubmissionStatus.Sent
                ? null
                : $"Delivery ended with status {delivered.EmailDeliveryStatus}.");
    }

    private static bool IsMatch(
        NormalizedInterestFormSubmission candidate,
        NormalizedInterestFormSubmission request,
        DateTimeOffset originalProcessedOnUtc)
    {
        var candidateDate = candidate.ReceivedOnUtc != default
            ? candidate.ReceivedOnUtc
            : candidate.CosmosTimestampOnUtc;
        if (candidateDate is null
            || (candidateDate.Value - originalProcessedOnUtc).Duration() > MatchDateTolerance)
        {
            return false;
        }

        var requestedEmails = EmailValues(request);
        if (requestedEmails.Count == 0 || !requestedEmails.Overlaps(EmailValues(candidate)))
        {
            return false;
        }

        var corroboratingMatches = 0;
        corroboratingMatches += Matches(request.Name, candidate.Name);
        corroboratingMatches += Matches(request.SubmissionType, candidate.SubmissionType);
        corroboratingMatches += Matches(request.CountryOfResidence, candidate.CountryOfResidence);
        corroboratingMatches += Matches(request.State, candidate.State);
        corroboratingMatches += Matches(request.City, candidate.City);
        corroboratingMatches += Matches(request.Zipcode, candidate.Zipcode);
        corroboratingMatches += Matches(request.StudentPhone, candidate.StudentPhone);
        corroboratingMatches += Matches(request.ParentPhone, candidate.ParentPhone);
        corroboratingMatches += Matches(request.ContactPhone, candidate.ContactPhone);
        corroboratingMatches += Matches(request.School, candidate.School);
        corroboratingMatches += Matches(request.ParentEnteredSchool, candidate.ParentEnteredSchool);
        corroboratingMatches += Matches(request.Age, candidate.Age);
        corroboratingMatches += Matches(request.ParentEnteredAge, candidate.ParentEnteredAge);

        return corroboratingMatches >= MinimumCorroboratingFieldMatches;
    }

    private static HashSet<string> EmailValues(NormalizedInterestFormSubmission submission)
    {
        return new[] { submission.StudentEmail, submission.ParentEmail, submission.ContactEmail }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int Matches(string? expected, string? actual)
    {
        return !string.IsNullOrWhiteSpace(expected)
            && !string.IsNullOrWhiteSpace(actual)
            && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
    }

    private static CatchUpSubmissionItemResult Result(
        int index,
        CatchUpSubmissionStatus status,
        string? submissionId,
        string? message)
    {
        return new CatchUpSubmissionItemResult
        {
            Index = index,
            Status = status,
            SubmissionId = submissionId,
            Message = message
        };
    }
}
