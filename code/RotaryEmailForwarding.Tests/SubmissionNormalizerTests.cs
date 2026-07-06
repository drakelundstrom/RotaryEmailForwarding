using RotaryEmailForwarding.FunctionApp.Models;
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
        Assert.Equal("Pending", normalized.EmailDeliveryStatus);
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
}
