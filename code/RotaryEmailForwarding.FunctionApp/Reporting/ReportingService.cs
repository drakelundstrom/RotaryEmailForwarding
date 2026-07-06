using System.Globalization;
using System.Text.Json.Serialization;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Reporting;

public sealed record SubmissionsByMonth
{
    [JsonPropertyName("Month")]
    public required string Month { get; init; }

    [JsonPropertyName("CountryResults")]
    public required IReadOnlyList<CountryResults> CountryResults { get; init; }
}

public sealed record CountryResults
{
    [JsonPropertyName("Name")]
    public required string Name { get; init; }

    [JsonPropertyName("NumberOfSubmissions")]
    public required int NumberOfSubmissions { get; init; }
}

public sealed class ReportingService(IApplicationRepository repository)
{
    public async Task<IReadOnlyList<SubmissionsByMonth>> GenerateSubmissionsByMonthAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var firstMonth = new DateTimeOffset(startUtc.Year, startUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var finalMonth = new DateTimeOffset(endUtc.Year, endUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var exclusiveEnd = finalMonth.AddMonths(1);
        var submissions = await repository.GetSubmissionsByReceivedRangeAsync(firstMonth, exclusiveEnd, cancellationToken);

        var buckets = new List<SubmissionsByMonth>();
        for (var cursor = firstMonth; cursor <= finalMonth; cursor = cursor.AddMonths(1))
        {
            var nextMonth = cursor.AddMonths(1);
            var monthlySubmissions = submissions
                .Where(submission => submission.ReceivedOnUtc >= cursor && submission.ReceivedOnUtc < nextMonth)
                .ToList();

            buckets.Add(new SubmissionsByMonth
            {
                Month = cursor.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
                CountryResults =
                [
                    CountCountry(monthlySubmissions, "USA", "usa"),
                    CountCountry(monthlySubmissions, "CANADA", "canada"),
                    CountCountry(monthlySubmissions, "MEXICO", "mexico"),
                    new CountryResults
                    {
                        Name = "other country",
                        NumberOfSubmissions = monthlySubmissions.Count(submission =>
                            !IsCountry(submission, "usa")
                            && !IsCountry(submission, "canada")
                            && !IsCountry(submission, "mexico"))
                    }
                ]
            });
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

    private static CountryResults CountCountry(
        IReadOnlyList<NormalizedInterestFormSubmission> submissions,
        string displayName,
        string normalizedCountry)
    {
        return new CountryResults
        {
            Name = displayName,
            NumberOfSubmissions = submissions.Count(submission => IsCountry(submission, normalizedCountry))
        };
    }

    private static bool IsCountry(NormalizedInterestFormSubmission submission, string normalizedCountry)
    {
        return string.Equals(submission.CountryOfResidence, normalizedCountry, StringComparison.OrdinalIgnoreCase);
    }
}
