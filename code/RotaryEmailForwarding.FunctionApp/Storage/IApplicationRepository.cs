using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;

namespace RotaryEmailForwarding.FunctionApp.Storage;

public interface IApplicationRepository
{
    Task StoreRawRequestAsync(RequestBodyLog requestLog, CancellationToken cancellationToken);

    Task InsertSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken);

    Task UpdateSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken);

    Task<NormalizedInterestFormSubmission?> GetSubmissionAsync(string id, CancellationToken cancellationToken);

    Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetRetryableUnsentSubmissionsAsync(
        DateTimeOffset retryWindowStartUtc,
        DateTimeOffset retryWindowEndUtc,
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByDistrictAsync(
        string districtName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByReceivedRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContactsForDistrict>> GetEffectiveDistrictContactsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);

    Task<ContactsForDistrict?> GetEffectiveDistrictContactByNameAsync(
        string districtName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);

    Task<ContactsForCountry?> GetEffectiveCountryContactAsync(
        string normalizedCountry,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);

    Task UpsertDistrictContactsAsync(
        IReadOnlyList<ContactsForDistrict> contacts,
        CancellationToken cancellationToken);

    Task UpsertCountryContactsAsync(
        IReadOnlyList<ContactsForCountry> contacts,
        CancellationToken cancellationToken);
}
