using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace RotaryEmailForwarding.FunctionApp.Domain;

public sealed record ContactsForDistrict
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public string Type { get; init; } = "ContactsForDistrict";

    public required string DistrictName { get; init; }

    public IReadOnlyList<string> EmailAddresses { get; init; } = [];

    public IReadOnlyList<string> Zipcodes { get; init; } = [];

    public int Version { get; init; } = 1;

    public DateTimeOffset EffectiveFromUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EffectiveToUtc { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record ContactsForCountry
{
    [JsonPropertyName("id")]
    [JsonProperty("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("D");

    public string Type { get; init; } = "ContactsForCountry";

    public required string CountryName { get; init; }

    public IReadOnlyList<string> EmailAddresses { get; init; } = [];

    public bool IsCertified { get; init; }

    public int Version { get; init; } = 1;

    public DateTimeOffset EffectiveFromUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EffectiveToUtc { get; init; }

    public bool IsActive { get; init; } = true;
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
