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
    [InlineData("The United Kingdom.", "SW1A 1AA", "uk", "SW1A 1AA")]
    public void Normalize_AppliesLegacyCountryAndZipcodeCompatibilityRules(
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
                OptionalSubmissionQuestion = "Can I choose a country?"
            },
            DateTimeOffset.UtcNow);

        Assert.Equal("16", normalized.Age);
        Assert.Equal("student@example.com", normalized.Email);
        Assert.Equal("555-0100", normalized.Phone);
        Assert.Equal("parent@example.com", normalized.ParentEmail);
        Assert.Equal("Can I choose a country?", normalized.SubmissionQuestion);
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

        Assert.Equal("15", normalized.Age);
        Assert.Equal("parent-contact@example.com", normalized.Email);
        Assert.Equal("555-0102", normalized.Phone);
    }
}
