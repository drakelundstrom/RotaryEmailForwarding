using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Reporting;

public sealed record MonthlySubmissionBucket(int Year, int Month, int Count);

public sealed class ReportingService(IApplicationRepository repository)
{
    public async Task<IReadOnlyList<MonthlySubmissionBucket>> GenerateSubmissionsByMonthAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var firstMonth = new DateTimeOffset(startUtc.Year, startUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var finalMonth = new DateTimeOffset(endUtc.Year, endUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var exclusiveEnd = finalMonth.AddMonths(1);
        var submissions = await repository.GetSubmissionsByReceivedRangeAsync(firstMonth, exclusiveEnd, cancellationToken);
        var counts = submissions
            .GroupBy(submission => new { submission.ReceivedOnUtc.Year, submission.ReceivedOnUtc.Month })
            .ToDictionary(group => (group.Key.Year, group.Key.Month), group => group.Count());

        var buckets = new List<MonthlySubmissionBucket>();
        for (var cursor = firstMonth; cursor <= finalMonth; cursor = cursor.AddMonths(1))
        {
            counts.TryGetValue((cursor.Year, cursor.Month), out var count);
            buckets.Add(new MonthlySubmissionBucket(cursor.Year, cursor.Month, count));
        }

        return buckets;
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByDistrictAsync(
        string districtName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        return repository.GetSubmissionsByDistrictAsync(districtName, sinceUtc, cancellationToken);
    }
}
