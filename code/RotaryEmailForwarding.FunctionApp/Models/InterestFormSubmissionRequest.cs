using System.Text.Json;
using System.Text.Json.Serialization;

namespace RotaryEmailForwarding.FunctionApp.Models;

public sealed record InterestFormSubmissionRequest
{
    public string? SubmissionType { get; init; }

    public string? IsInterestedOutboundStudent { get; init; }

    public string? IsInterestedInHosting { get; init; }

    public string? SubmissionQuestion { get; init; }

    public string? OptionalSubmissionQuestion { get; init; }

    public string? Name { get; init; }

    public string? Age { get; init; }

    public string? ParentEnteredAge { get; init; }

    public string? Gender { get; init; }

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? StudentEmail { get; init; }

    public string? StudentPhone { get; init; }

    public string? ParentEmail { get; init; }

    public string? ParentPhone { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public string? CountryOfResidence { get; init; }

    public string? State { get; init; }

    public string? City { get; init; }

    public string? Zipcode { get; init; }

    public string? CountryChoiceOne { get; init; }

    public string? CountryChoiceTwo { get; init; }

    public string? CountryChoiceThree { get; init; }

    public string? CountryChoiceFour { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}
