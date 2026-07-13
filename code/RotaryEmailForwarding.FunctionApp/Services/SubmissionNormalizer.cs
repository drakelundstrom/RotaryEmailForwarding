using System.Globalization;
using System.Text.Json;
using RotaryEmailForwarding.FunctionApp.Models;

namespace RotaryEmailForwarding.FunctionApp.Services;

public enum InterestFormSubmitterType
{
    Unknown,
    Student,
    Parent,
    Rotarian,
    Other
}

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
        var submitterType = GetSubmitterType(request.SubmissionType);
        var studentEmail = TrimToNull(request.StudentEmail);
        var studentPhone = TrimToNull(request.StudentPhone);
        var parentEmail = TrimToNull(request.ParentEmail);
        var parentPhone = TrimToNull(request.ParentPhone);
        var contactEmail = TrimToNull(request.ContactEmail);
        var contactPhone = TrimToNull(request.ContactPhone);
        var legacyEmail = TrimToNull(request.Email);
        var legacyPhone = TrimToNull(request.Phone);

        return new NormalizedInterestFormSubmission
        {
            Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            SubmissionType = TrimToNull(request.SubmissionType),
            IsInterestedOutboundStudent = NormalizeBoolean(request.IsInterestedOutboundStudent),
            IsInterestedInHosting = NormalizeBoolean(request.IsInterestedInHosting),
            SubmissionQuestion = TrimToNull(request.OptionalSubmissionQuestion) ?? TrimToNull(request.SubmissionQuestion),
            Name = TrimToNull(request.Name),
            Age = GetStudentAge(request, submitterType),
            Gender = TrimToNull(request.Gender),
            Email = GetPrimaryEmail(submitterType, request.SubmissionType, studentEmail, contactEmail, legacyEmail),
            Phone = GetPrimaryPhone(submitterType, request.SubmissionType, studentPhone, contactPhone, legacyPhone),
            StudentEmail = studentEmail,
            StudentPhone = studentPhone,
            ParentEmail = parentEmail,
            ParentPhone = parentPhone,
            ContactEmail = contactEmail,
            ContactPhone = contactPhone,
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

    public static InterestFormSubmitterType GetSubmitterType(string? submissionType)
    {
        var normalized = NormalizeOptionKey(submissionType);
        if (normalized is null)
        {
            return InterestFormSubmitterType.Unknown;
        }

        if (normalized.Contains("rotarian", StringComparison.Ordinal))
        {
            return InterestFormSubmitterType.Rotarian;
        }

        if (normalized.Contains("parent", StringComparison.Ordinal)
            || normalized.Contains("guardian", StringComparison.Ordinal))
        {
            return InterestFormSubmitterType.Parent;
        }

        if (normalized.Contains("student", StringComparison.Ordinal))
        {
            return InterestFormSubmitterType.Student;
        }

        return normalized.Contains("other", StringComparison.Ordinal)
            ? InterestFormSubmitterType.Other
            : InterestFormSubmitterType.Unknown;
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

    private static string? GetStudentAge(
        InterestFormSubmissionRequest request,
        InterestFormSubmitterType submitterType)
    {
        return submitterType switch
        {
            InterestFormSubmitterType.Student => TrimToNull(request.Age),
            InterestFormSubmitterType.Parent => TrimToNull(request.ParentEnteredAge),
            InterestFormSubmitterType.Unknown when string.IsNullOrWhiteSpace(request.SubmissionType) => TrimToNull(request.Age),
            _ => null
        };
    }

    private static string? GetPrimaryEmail(
        InterestFormSubmitterType submitterType,
        string? submissionType,
        string? studentEmail,
        string? contactEmail,
        string? legacyEmail)
    {
        return submitterType switch
        {
            InterestFormSubmitterType.Student => studentEmail ?? legacyEmail,
            InterestFormSubmitterType.Unknown when string.IsNullOrWhiteSpace(submissionType) =>
                legacyEmail ?? contactEmail ?? studentEmail,
            _ => contactEmail ?? legacyEmail
        };
    }

    private static string? GetPrimaryPhone(
        InterestFormSubmitterType submitterType,
        string? submissionType,
        string? studentPhone,
        string? contactPhone,
        string? legacyPhone)
    {
        return submitterType switch
        {
            InterestFormSubmitterType.Student => studentPhone ?? legacyPhone,
            InterestFormSubmitterType.Unknown when string.IsNullOrWhiteSpace(submissionType) =>
                legacyPhone ?? contactPhone ?? studentPhone,
            _ => contactPhone ?? legacyPhone
        };
    }

    private static string? NormalizeOptionKey(string? value)
    {
        var trimmed = TrimToNull(value);
        if (trimmed is null)
        {
            return null;
        }

        return new string(trimmed
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string TakeAtMost(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }
}
