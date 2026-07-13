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
        var submissions = await repository.GetSubmissionsByStorageTimestampRangeAsync(
            quarters[0].Start,
            quarters[^1].End,
            cancellationToken);
        var districtContacts = await repository.GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        var districtLookup = BuildDistrictLookup(districtContacts);
        var rows = new Dictionary<QuarterlyDistrictReportRow, int[]>();

        foreach (var submission in submissions)
        {
            if (submission.CosmosTimestampOnUtc is not { } storageTimestamp)
            {
                continue;
            }

            var quarterIndex = quarters.FindIndex(quarter =>
                storageTimestamp >= quarter.Start
                && storageTimestamp < quarter.End);
            if (quarterIndex < 0)
            {
                continue;
            }

            var country = CountryGroup(submission.CountryOfResidence);
            foreach (var district in DistrictGroups(submission, districtLookup))
            {
                var row = new QuarterlyDistrictReportRow(country, district);
                if (!rows.TryGetValue(row, out var counts))
                {
                    counts = new int[quarters.Count];
                    rows[row] = counts;
                }

                counts[quarterIndex]++;
            }
        }

        return ToMarkdownTable(quarters, rows);
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

    private static IReadOnlyList<Quarter> BuildQuarterWindow(DateTimeOffset asOfUtc)
    {
        var currentQuarter = QuarterStart(asOfUtc);
        var start = currentQuarter.AddYears(-2);
        var end = currentQuarter.AddMonths(3);
        var quarters = new List<Quarter>();

        for (var cursor = start; cursor < end; cursor = cursor.AddMonths(3))
        {
            quarters.Add(new Quarter(cursor, cursor.AddMonths(3), QuarterLabel(cursor)));
        }

        return quarters;
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

        var routedDistricts = submission.RoutedDistricts
            .Where(district => !string.IsNullOrWhiteSpace(district))
            .Select(district => district.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (routedDistricts.Count > 0)
        {
            return routedDistricts;
        }

        var zipcode = SubmissionNormalizer.NormalizeZipcode(submission.Zipcode, normalizedCountry);
        if (zipcode is not null
            && districtLookup.TryGetValue(DistrictLookupKey(normalizedCountry, zipcode), out var districts)
            && districts.Count > 0)
        {
            return districts;
        }

        return [OtherDistrict];
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
        IReadOnlyList<Quarter> quarters,
        Dictionary<QuarterlyDistrictReportRow, int[]> rows)
    {
        var builder = new StringBuilder();
        builder
            .Append("| Country | District | ")
            .AppendJoin(" | ", quarters.Select(quarter => MarkdownCell(quarter.Label)))
            .AppendLine(" |");
        builder
            .Append("| --- | --- | ")
            .AppendJoin(" | ", quarters.Select(_ => "---:"))
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

    private sealed record Quarter(DateTimeOffset Start, DateTimeOffset End, string Label);

    private sealed record DistrictZipEntry(string Key, string District);

    private sealed record QuarterlyDistrictReportRow(string Country, string District);
}
