using System.Text.Json;
using System.Text.Json.Serialization;

namespace RotaryEmailForwarding.FunctionApp.Models;

public sealed record InterestFormSubmissionRequest
{
    public string? SubmissionType { get; init; }

    public string? SubmissionQuestion { get; init; }

    // Compatibility for clients that used the previous API property name.
    public string? OptionalSubmissionQuestion { get; init; }

    public string? Name { get; init; }

    public string? Age { get; init; }

    public string? ParentEnteredAge { get; init; }

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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnhandledFields { get; init; }
}
