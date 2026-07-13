using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RotaryEmailForwarding.FunctionApp.Domain;

public sealed record ContactsForDistrict
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public string Type { get; init; } = "ContactsForDistrict";

    public required string Country { get; init; }

    public required string District { get; init; }

    public IReadOnlyList<string> EmailAddresses { get; init; } = [];

    public IReadOnlyList<string> ZipCodes { get; init; } = [];
}

public sealed record ContactsForCountry
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public string Type { get; init; } = "ContactsForCountry";

    public required string Country { get; init; }

    public IReadOnlyList<string> EmailAddresses { get; init; } = [];

    public bool IsCertified { get; init; }
}

public sealed record RequestBodyLog
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public string Type { get; init; } = "RequestBodyLog";

    public required string CorrelationId { get; init; }

    public required string RequestBody { get; init; }

    public required DateTimeOffset ReceivedOnUtc { get; init; }
}
