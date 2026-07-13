using System.Globalization;
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
        ["mx"] = "mexico"
    };

    public static NormalizedInterestFormSubmission Normalize(
        InterestFormSubmissionRequest request,
        DateTimeOffset receivedOnUtc)
    {
        var normalizedCountry = NormalizeCountry(request.CountryOfResidence);
        var normalizedZipcode = NormalizeZipcode(request.Zipcode, normalizedCountry);
        var submitterType = GetSubmitterType(request.SubmissionType);

        return new NormalizedInterestFormSubmission
        {
            Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            SubmissionType = TrimToNull(request.SubmissionType),
            OptionalSubmissionQuestion = TrimToNull(request.OptionalSubmissionQuestion),
            Name = TrimToNull(request.Name),
            Age = GetStudentAge(request, submitterType),
            StudentEmail = KeepForStudent(request.StudentEmail, submitterType),
            StudentPhone = KeepForStudent(request.StudentPhone, submitterType),
            ParentEmail = KeepForStudent(request.ParentEmail, submitterType),
            ParentPhone = KeepForStudent(request.ParentPhone, submitterType),
            ContactEmail = KeepForNonStudent(request.ContactEmail, submitterType),
            ContactPhone = KeepForNonStudent(request.ContactPhone, submitterType),
            CountryOfResidence = normalizedCountry,
            State = TrimToNull(request.State),
            City = TrimToNull(request.City),
            Zipcode = normalizedZipcode,
            ReceivedOnUtc = receivedOnUtc
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

    private static string? KeepForStudent(string? value, InterestFormSubmitterType submitterType)
    {
        return submitterType == InterestFormSubmitterType.Student
            ? TrimToNull(value)
            : null;
    }

    private static string? KeepForNonStudent(string? value, InterestFormSubmitterType submitterType)
    {
        return submitterType == InterestFormSubmitterType.Student
            ? null
            : TrimToNull(value);
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
