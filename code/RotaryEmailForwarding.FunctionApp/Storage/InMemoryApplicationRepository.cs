using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Storage;

public sealed class InMemoryApplicationRepository : IApplicationRepository
{
    private readonly List<RequestBodyLog> requestLogs = [];
    private readonly List<NormalizedInterestFormSubmission> submissions = [];
    private readonly List<ContactsForDistrict> districtContacts = [];
    private readonly List<ContactsForCountry> countryContacts = [];
    private readonly object gate = new();

    public Task StoreRawRequestAsync(RequestBodyLog requestLog, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            requestLogs.Add(requestLog);
        }

        return Task.CompletedTask;
    }

    public Task InsertSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            submissions.Add(submission);
        }

        return Task.CompletedTask;
    }

    public Task UpdateSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var index = submissions.FindIndex(existing => existing.Id == submission.Id);
            if (index >= 0)
            {
                submissions[index] = submission;
            }
            else
            {
                submissions.Add(submission);
            }
        }

        return Task.CompletedTask;
    }

    public Task<NormalizedInterestFormSubmission?> GetSubmissionAsync(string id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(submissions.FirstOrDefault(submission => submission.Id == id));
        }
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetRetryableUnsentSubmissionsAsync(
        DateTimeOffset retryWindowStartUtc,
        DateTimeOffset retryWindowEndUtc,
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var results = submissions
                .Where(submission => submission.SentOnUtc is null)
                .Where(submission => submission.EmailDeliveryStatus is EmailDeliveryStatus.Pending or EmailDeliveryStatus.RetryPending)
                .Where(submission => submission.NextEmailAttemptOnUtc is null || submission.NextEmailAttemptOnUtc <= nowUtc)
                .Where(submission => submission.ReceivedOnUtc < retryWindowEndUtc)
                .OrderBy(submission => submission.ReceivedOnUtc)
                .Take(maxCount)
                .ToList();

            return Task.FromResult<IReadOnlyList<NormalizedInterestFormSubmission>>(results);
        }
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByDistrictAsync(
        string districtName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var district = EffectiveDistrictContacts(DateTimeOffset.UtcNow)
                .FirstOrDefault(contact => string.Equals(contact.District, districtName, StringComparison.OrdinalIgnoreCase));
            if (district is null || district.ZipCodes.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<NormalizedInterestFormSubmission>>([]);
            }

            var normalizedCountry = SubmissionNormalizer.NormalizeCountry(district.Country);
            var normalizedZipCodes = district.ZipCodes
                .Select(zip => SubmissionNormalizer.NormalizeZipcode(zip, normalizedCountry))
                .Where(zip => !string.IsNullOrWhiteSpace(zip))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var results = submissions
                .Where(submission => submission.ReceivedOnUtc >= sinceUtc)
                .Where(submission => string.Equals(submission.CountryOfResidence, normalizedCountry, StringComparison.OrdinalIgnoreCase))
                .Where(submission => submission.Zipcode is not null && normalizedZipCodes.Contains(submission.Zipcode))
                .OrderByDescending(submission => submission.ReceivedOnUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<NormalizedInterestFormSubmission>>(results);
        }
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByStorageTimestampRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var results = submissions
                .Where(submission =>
                    submission.CosmosTimestampOnUtc is { } timestamp
                    && timestamp >= startUtc
                    && timestamp < endUtc)
                .OrderBy(submission => submission.CosmosTimestampOnUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<NormalizedInterestFormSubmission>>(results);
        }
    }

    public Task<IReadOnlyList<ContactsForDistrict>> GetEffectiveDistrictContactsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var results = EffectiveDistrictContacts(asOfUtc);

            return Task.FromResult<IReadOnlyList<ContactsForDistrict>>(results);
        }
    }

    public async Task<ContactsForDistrict?> GetEffectiveDistrictContactByNameAsync(
        string districtName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        return contacts.FirstOrDefault(contact => string.Equals(contact.District, districtName, StringComparison.OrdinalIgnoreCase));
    }

    public Task<ContactsForCountry?> GetEffectiveCountryContactAsync(
        string normalizedCountry,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var result = countryContacts
                .Where(contact => string.Equals(contact.Country, normalizedCountry, StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();

            return Task.FromResult(result);
        }
    }

    public Task UpsertDistrictContactsAsync(
        IReadOnlyList<ContactsForDistrict> contacts,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            districtContacts.AddRange(contacts);
        }

        return Task.CompletedTask;
    }

    public Task UpsertCountryContactsAsync(
        IReadOnlyList<ContactsForCountry> contacts,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            countryContacts.AddRange(contacts);
        }

        return Task.CompletedTask;
    }

    private List<ContactsForDistrict> EffectiveDistrictContacts(DateTimeOffset asOfUtc)
    {
        return districtContacts
            .GroupBy(contact => $"{SubmissionNormalizer.NormalizeCountry(contact.Country)}:{contact.District}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(contact => contact.Country, StringComparer.OrdinalIgnoreCase)
            .ThenBy(contact => contact.District, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
