using RotaryEmailForwarding.FunctionApp.Domain;

namespace RotaryEmailForwarding.FunctionApp.Routing;

public enum SubmissionRouteKind
{
    District,
    Country,
    UncertifiedCountry,
    Fallback
}

public sealed record SubmissionRoute
{
    public required SubmissionRouteKind Kind { get; init; }

    public IReadOnlyList<ContactsForDistrict> DistrictContacts { get; init; } = [];

    public ContactsForCountry? CountryContact { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasMultipleDistrictMatches => DistrictContacts.Count > 1;
}
