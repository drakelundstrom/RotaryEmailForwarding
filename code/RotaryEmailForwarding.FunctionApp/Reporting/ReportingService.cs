using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;
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
    private static readonly DateTimeOffset SentReportStartUtc =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Usa = "USA";
    private const string Canada = "Canada";
    private const string Mexico = "Mexico";
    private const string OtherCountries = "Other countries";
    private const string OtherDistrict = "Other";

    public async Task<IReadOnlyList<SubmissionsByMonth>> GenerateSubmissionsByMonthAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        var firstMonth = new DateTimeOffset(startUtc.Year, startUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var finalMonth = new DateTimeOffset(endUtc.Year, endUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var exclusiveEnd = finalMonth.AddMonths(1);
        var submissions = await repository.GetSubmissionsByStorageTimestampRangeAsync(firstMonth, exclusiveEnd, cancellationToken);

        var buckets = new List<SubmissionsByMonth>();
        for (var cursor = firstMonth; cursor <= finalMonth; cursor = cursor.AddMonths(1))
        {
            var nextMonth = cursor.AddMonths(1);
            var monthlySubmissions = submissions
                .Where(submission =>
                    submission.CosmosTimestampOnUtc is { } timestamp
                    && timestamp >= cursor
                    && timestamp < nextMonth)
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

    public async Task<string> GenerateInterestFormsByDistrictQuarterMarkdownAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var quarters = BuildQuarterWindow(asOfUtc).ToList();
        var submissions = await repository.GetSubmissionsByReceivedOnOrStorageTimestampRangeAsync(
            quarters[0].Start,
            quarters[^1].End,
            cancellationToken);
        return await GenerateInterestFormsByDistrictQuarterMarkdownAsync(
            asOfUtc,
            quarters,
            submissions,
            ReportedOnUtc,
            cancellationToken);
    }

    public async Task<string> GenerateSentInterestFormsByDistrictMonthMarkdownAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var months = BuildSentMonthWindow(asOfUtc).ToList();
        var submissions = await repository.GetSubmissionsBySentOnRangeAsync(
            months[0].Start,
            months[^1].End,
            cancellationToken);
        return await GenerateInterestFormsByDistrictQuarterMarkdownAsync(
            asOfUtc,
            months,
            submissions,
            submission => submission.SentOnUtc,
            cancellationToken);
    }

    private async Task<string> GenerateInterestFormsByDistrictQuarterMarkdownAsync(
        DateTimeOffset asOfUtc,
        List<ReportPeriod> periods,
        IReadOnlyList<NormalizedInterestFormSubmission> submissions,
        Func<NormalizedInterestFormSubmission, DateTimeOffset?> reportDate,
        CancellationToken cancellationToken)
    {
        var districtContacts = await repository.GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        var districtLookup = BuildDistrictLookup(districtContacts);
        var rows = new Dictionary<DistrictReportRow, int[]>();

        foreach (var submission in submissions)
        {
            if (reportDate(submission) is not { } reportedOnUtc)
            {
                continue;
            }

            var periodIndex = periods.FindIndex(period =>
                reportedOnUtc >= period.Start
                && reportedOnUtc < period.End);
            if (periodIndex < 0)
            {
                continue;
            }

            var country = CountryGroup(submission.CountryOfResidence);
            foreach (var district in DistrictGroups(submission, districtLookup))
            {
                var row = new DistrictReportRow(country, district);
                if (!rows.TryGetValue(row, out var counts))
                {
                    counts = new int[periods.Count];
                    rows[row] = counts;
                }

                counts[periodIndex]++;
            }
        }

        return ToMarkdownTable(periods, rows);
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
        return string.Equals(
            SubmissionNormalizer.NormalizeCountry(submission.CountryOfResidence),
            normalizedCountry,
            StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ReportPeriod> BuildQuarterWindow(DateTimeOffset asOfUtc)
    {
        var currentQuarter = QuarterStart(asOfUtc);
        var start = currentQuarter.AddYears(-2);
        var end = currentQuarter.AddMonths(3);
        var quarters = new List<ReportPeriod>();

        for (var cursor = start; cursor < end; cursor = cursor.AddMonths(3))
        {
            quarters.Add(new ReportPeriod(cursor, cursor.AddMonths(3), QuarterLabel(cursor)));
        }

        return quarters;
    }

    private static IReadOnlyList<ReportPeriod> BuildSentMonthWindow(DateTimeOffset asOfUtc)
    {
        var currentMonth = MonthStart(asOfUtc);
        var end = currentMonth.AddMonths(1);
        var months = new List<ReportPeriod>();

        for (var cursor = SentReportStartUtc; cursor < end; cursor = cursor.AddMonths(1))
        {
            months.Add(new ReportPeriod(
                cursor,
                cursor.AddMonths(1),
                cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
        }

        return months;
    }

    private static DateTimeOffset MonthStart(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset QuarterStart(DateTimeOffset value)
    {
        var quarterStartMonth = (((value.Month - 1) / 3) * 3) + 1;

        return new DateTimeOffset(value.Year, quarterStartMonth, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static string QuarterLabel(DateTimeOffset quarterStart)
    {
        var quarter = ((quarterStart.Month - 1) / 3) + 1;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{quarterStart.Year} Q{quarter}");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDistrictLookup(
        IReadOnlyList<ContactsForDistrict> contacts)
    {
        return contacts
            .SelectMany(DistrictZipEntries)
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(entry => entry.District)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(DistrictSortKey, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(district => district, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<DistrictZipEntry> DistrictZipEntries(ContactsForDistrict contact)
    {
        var normalizedCountry = SubmissionNormalizer.NormalizeCountry(contact.Country);
        if (normalizedCountry is not ("usa" or "canada"))
        {
            yield break;
        }

        foreach (var zipCode in contact.ZipCodes)
        {
            var normalizedZipcode = SubmissionNormalizer.NormalizeZipcode(zipCode, normalizedCountry);
            if (!string.IsNullOrWhiteSpace(normalizedZipcode))
            {
                yield return new DistrictZipEntry(
                    DistrictLookupKey(normalizedCountry, normalizedZipcode),
                    string.IsNullOrWhiteSpace(contact.District) ? OtherDistrict : contact.District.Trim());
            }
        }
    }

    private static IReadOnlyList<string> DistrictGroups(
        NormalizedInterestFormSubmission submission,
        IReadOnlyDictionary<string, IReadOnlyList<string>> districtLookup)
    {
        var normalizedCountry = SubmissionNormalizer.NormalizeCountry(submission.CountryOfResidence);
        if (normalizedCountry is not ("usa" or "canada"))
        {
            return [OtherDistrict];
        }

        var zipcode = SubmissionNormalizer.NormalizeZipcode(submission.Zipcode, normalizedCountry);
        if (normalizedCountry is "usa" && zipcode is "321")
        {
            return [];
        }

        var routedDistricts = submission.RoutedDistricts
            .Where(district => !string.IsNullOrWhiteSpace(district))
            .Select(district => district.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (routedDistricts.Count > 0)
        {
            return routedDistricts
                .Where(district => !IsIgnoredReportDistrict(district))
                .ToList();
        }

        if (zipcode is not null
            && districtLookup.TryGetValue(DistrictLookupKey(normalizedCountry, zipcode), out var districts)
            && districts.Count > 0)
        {
            return districts
                .Where(district => !IsIgnoredReportDistrict(district))
                .ToList();
        }

        return [OtherDistrict];
    }

    private static bool IsIgnoredReportDistrict(string district)
    {
        var normalized = district.Trim();
        if (normalized.StartsWith("district", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["district".Length..].Trim();
        }

        return normalized is "123" or "321";
    }

    private static DateTimeOffset? ReportedOnUtc(NormalizedInterestFormSubmission submission)
    {
        return submission.ReceivedOnUtc != default
            ? submission.ReceivedOnUtc
            : submission.CosmosTimestampOnUtc;
    }

    private static string CountryGroup(string? country)
    {
        return SubmissionNormalizer.NormalizeCountry(country) switch
        {
            "usa" => Usa,
            "canada" => Canada,
            "mexico" => Mexico,
            _ => OtherCountries
        };
    }

    private static string DistrictLookupKey(string country, string zipcode)
    {
        return $"{country}:{zipcode}";
    }

    private static string ToMarkdownTable(
        IReadOnlyList<ReportPeriod> periods,
        Dictionary<DistrictReportRow, int[]> rows)
    {
        var builder = new StringBuilder();
        builder
            .Append("| Country | District | ")
            .AppendJoin(" | ", periods.Select(period => MarkdownCell(period.Label)))
            .AppendLine(" |");
        builder
            .Append("| --- | --- | ")
            .AppendJoin(" | ", periods.Select(_ => "---:"))
            .AppendLine(" |");

        foreach (var row in rows
            .OrderBy(row => CountrySortOrder(row.Key.Country))
            .ThenBy(row => DistrictSortKey(row.Key.District), StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Key.District, StringComparer.OrdinalIgnoreCase))
        {
            builder
                .Append("| ")
                .Append(MarkdownCell(row.Key.Country))
                .Append(" | ")
                .Append(MarkdownCell(row.Key.District))
                .Append(" | ")
                .AppendJoin(" | ", row.Value)
                .AppendLine(" |");
        }

        return builder.ToString();
    }

    private static int CountrySortOrder(string country)
    {
        return country switch
        {
            Usa => 0,
            Canada => 1,
            Mexico => 2,
            OtherCountries => 3,
            _ => 4
        };
    }

    private static string DistrictSortKey(string district)
    {
        return string.Equals(district, OtherDistrict, StringComparison.OrdinalIgnoreCase)
            ? "~"
            : district;
    }

    private static string MarkdownCell(string value)
    {
        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);
    }

    private sealed record ReportPeriod(DateTimeOffset Start, DateTimeOffset End, string Label);

    private sealed record DistrictZipEntry(string Key, string District);

    private sealed record DistrictReportRow(string Country, string District);
}
