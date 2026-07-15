using System.Text.Json;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Services;
using Xunit;

namespace RotaryEmailForwarding.Tests;

public sealed class SubmissionNormalizerTests
{
    [Fact]
    public void Normalize_AllowsMissingOptionalPublicFormFields()
    {
        var receivedOnUtc = new DateTimeOffset(2026, 1, 15, 14, 25, 30, TimeSpan.Zero);

        var normalized = SubmissionNormalizer.Normalize(new InterestFormSubmissionRequest(), receivedOnUtc);

        Assert.Equal("InterestFormSubmission", normalized.Type);
        Assert.Equal(receivedOnUtc, normalized.ReceivedOnUtc);
        Assert.Equal(EmailDeliveryStatus.Pending, normalized.EmailDeliveryStatus);
        Assert.Null(normalized.SentOnUtc);
        Assert.Empty(normalized.Errors);
    }

    [Theory]
    [InlineData("United States of America", "44102-1234", "usa", "44102")]
    [InlineData("Canada", "m5v 2t6", "canada", "M5V")]
    [InlineData("Mexico", "01234", "mexico", "01234")]
    public void Normalize_AppliesSupportedCountryAndZipcodeRules(
        string country,
        string zipcode,
        string expectedCountry,
        string expectedZipcode)
    {
        var normalized = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                CountryOfResidence = country,
                Zipcode = zipcode
            },
            DateTimeOffset.UtcNow);

        Assert.Equal(expectedCountry, normalized.CountryOfResidence);
        Assert.Equal(expectedZipcode, normalized.Zipcode);
    }

    [Fact]
    public void Request_CollectsUnhandledFieldsWithoutAddingThemToNormalizedSubmission()
    {
        var request = JsonSerializer.Deserialize<InterestFormSubmissionRequest>(
            """
            {
              "SubmissionType": "Student",
              "Name": "Jordan Example",
              "StudentEmail": "student@example.com",
              "LegacyEmail": "legacy@example.com",
              "ExtraProgramAnswer": "yes"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(
            ["ExtraProgramAnswer", "LegacyEmail"],
            request.UnhandledFields!.Keys.OrderBy(key => key));

        var normalized = SubmissionNormalizer.Normalize(request, DateTimeOffset.UtcNow);
        var persistedPropertyNames = typeof(NormalizedInterestFormSubmission)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal("student@example.com", normalized.StudentEmail);
        Assert.DoesNotContain("UnhandledFields", persistedPropertyNames);
        Assert.DoesNotContain("LegacyEmail", persistedPropertyNames);
        Assert.DoesNotContain("Email", persistedPropertyNames);
    }

    [Fact]
    public void Request_MapsSubmissionQuestionIntoNormalizedSubmission()
    {
        var request = JsonSerializer.Deserialize<InterestFormSubmissionRequest>(
            """
            {
              "SubmissionType": "Student",
              "SubmissionQuestion": "Can I choose a country?"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal("Can I choose a country?", request.SubmissionQuestion);
        Assert.False(request.UnhandledFields?.ContainsKey("SubmissionQuestion") ?? false);

        var normalized = SubmissionNormalizer.Normalize(request, DateTimeOffset.UtcNow);

        Assert.Equal("Can I choose a country?", normalized.OptionalSubmissionQuestion);
    }

    [Fact]
    public void Normalize_PrefersSubmissionQuestionAndSupportsPreviousPropertyName()
    {
        var canonical = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                SubmissionQuestion = "Canonical question",
                OptionalSubmissionQuestion = "Previous question"
            },
            DateTimeOffset.UtcNow);
        var previous = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                OptionalSubmissionQuestion = "Previous question"
            },
            DateTimeOffset.UtcNow);

        Assert.Equal("Canonical question", canonical.OptionalSubmissionQuestion);
        Assert.Equal("Previous question", previous.OptionalSubmissionQuestion);
    }

    [Fact]
    public void Normalize_StudentSubmissionUsesStudentAndParentContactFields()
    {
        var normalized = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Student",
                Name = "Jordan Example",
                Age = "16",
                ParentEnteredAge = "15",
                StudentEmail = "student@example.com",
                StudentPhone = "555-0100",
                ParentEmail = "parent@example.com",
                ParentPhone = "555-0101",
                ContactEmail = "contact@example.com",
                ContactPhone = "555-0102",
                SubmissionQuestion = "Can I choose a country?"
            },
            DateTimeOffset.UtcNow);

        Assert.Equal("16", normalized.Age);
        Assert.Equal("student@example.com", normalized.StudentEmail);
        Assert.Equal("555-0100", normalized.StudentPhone);
        Assert.Equal("parent@example.com", normalized.ParentEmail);
        Assert.Equal("15", normalized.ParentEnteredAge);
        Assert.Equal("contact@example.com", normalized.ContactEmail);
        Assert.Equal("555-0102", normalized.ContactPhone);
        Assert.Equal("Can I choose a country?", normalized.OptionalSubmissionQuestion);
    }

    [Fact]
    public void Normalize_ParentSubmissionUsesParentEnteredAgeAndGenericContactFields()
    {
        var normalized = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Parent",
                Age = "16",
                ParentEnteredAge = "15",
                StudentEmail = "student@example.com",
                ContactEmail = "parent-contact@example.com",
                ContactPhone = "555-0102"
            },
            DateTimeOffset.UtcNow);

        Assert.Equal("16", normalized.Age);
        Assert.Equal("15", normalized.ParentEnteredAge);
        Assert.Equal("student@example.com", normalized.StudentEmail);
        Assert.Equal("parent-contact@example.com", normalized.ContactEmail);
        Assert.Equal("555-0102", normalized.ContactPhone);
    }
}
