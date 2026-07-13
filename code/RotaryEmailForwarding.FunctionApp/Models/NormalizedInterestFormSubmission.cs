using System.Text.Json.Serialization;
using Newtonsoft.Json;
using RotaryEmailForwarding.FunctionApp.Domain;

namespace RotaryEmailForwarding.FunctionApp.Models;

public sealed record NormalizedInterestFormSubmission
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public required string Id { get; init; }

    public string Type { get; init; } = "InterestFormSubmission";

    [JsonPropertyName("_ts")]
    [JsonProperty("_ts")]
    public long? CosmosTimestamp { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public DateTimeOffset? CosmosTimestampOnUtc =>
        CosmosTimestamp is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(CosmosTimestamp.Value)
            : null;

    public string? SubmissionType { get; init; }

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

    public required DateTimeOffset ReceivedOnUtc { get; init; }

    public DateTimeOffset? SentOnUtc { get; init; }

    public EmailDeliveryStatus EmailDeliveryStatus { get; init; } = EmailDeliveryStatus.Pending;

    public IReadOnlyList<OutboundEmailAttempt> EmailDeliveryAttempts { get; init; } = [];

    public DateTimeOffset? NextEmailAttemptOnUtc { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public string? CorrelationId { get; init; }

    public IReadOnlyList<string> RoutedDistricts { get; init; } = [];

    public string? RoutedCountry { get; init; }
}
