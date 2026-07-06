using System.Globalization;
using System.Text.Json;
using RotaryEmailForwarding.FunctionApp.Models;

namespace RotaryEmailForwarding.FunctionApp.Services;

public static class SubmissionNormalizer
{
    private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["unitedstatesofamerica"] = "usa",
        ["us"] = "usa",
        ["unitedstates"] = "usa",
        ["america"] = "usa",
        ["britian"] = "uk",
        ["unitedkingdom"] = "uk",
        ["england"] = "uk"
    };

    public static NormalizedInterestFormSubmission Normalize(
        InterestFormSubmissionRequest request,
        DateTimeOffset receivedOnUtc)
    {
        var normalizedCountry = NormalizeCountry(request.CountryOfResidence);
        var normalizedZipcode = NormalizeZipcode(request.Zipcode, normalizedCountry);

        return new NormalizedInterestFormSubmission
        {
            Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            IsInterestedOutboundStudent = NormalizeBoolean(request.IsInterestedOutboundStudent),
            IsInterestedInHosting = NormalizeBoolean(request.IsInterestedInHosting),
            SubmissionQuestion = TrimToNull(request.SubmissionQuestion),
            Name = TrimToNull(request.Name),
            Age = TrimToNull(request.Age),
            Gender = TrimToNull(request.Gender),
            Email = TrimToNull(request.Email),
            Phone = TrimToNull(request.Phone),
            CountryOfResidence = normalizedCountry,
            State = TrimToNull(request.State),
            City = TrimToNull(request.City),
            Zipcode = normalizedZipcode,
            CountryChoiceOne = TrimToNull(request.CountryChoiceOne),
            CountryChoiceTwo = TrimToNull(request.CountryChoiceTwo),
            CountryChoiceThree = TrimToNull(request.CountryChoiceThree),
            CountryChoiceFour = TrimToNull(request.CountryChoiceFour),
            ReceivedOnUtc = receivedOnUtc,
            AdditionalFields = request.AdditionalFields ?? new Dictionary<string, JsonElement>()
        };
    }

    public static string? NormalizeCountry(string? country)
    {
        var trimmed = TrimToNull(country);
        if (trimmed is null)
        {
            return null;
        }

        var lower = trimmed.ToLowerInvariant();
        var withoutLeadingArticle = lower.StartsWith("the ", StringComparison.Ordinal)
            ? lower[4..]
            : lower;

        var compact = withoutLeadingArticle
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return CountryAliases.TryGetValue(compact, out var canonicalCountry)
            ? canonicalCountry
            : compact;
    }

    public static string? NormalizeZipcode(string? zipcode, string? normalizedCountry)
    {
        var normalized = TrimToNull(zipcode)?.ToUpperInvariant();
        if (normalized is null)
        {
            return null;
        }

        return normalizedCountry switch
        {
            "usa" => TakeAtMost(normalized, 5),
            "canada" => TakeAtMost(normalized, 3),
            _ => normalized
        };
    }

    private static bool? NormalizeBoolean(string? value)
    {
        var normalized = TrimToNull(value)?.ToLowerInvariant();

        return normalized switch
        {
            "true" or "yes" or "y" or "1" or "on" => true,
            "false" or "no" or "n" or "0" or "off" => false,
            _ => null
        };
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string TakeAtMost(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
