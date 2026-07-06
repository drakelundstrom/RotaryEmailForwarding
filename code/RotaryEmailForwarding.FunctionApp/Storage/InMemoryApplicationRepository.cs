using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;

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
            var results = submissions
                .Where(submission => submission.ReceivedOnUtc >= sinceUtc)
                .Where(submission => submission.AdditionalFields.TryGetValue("routedDistricts", out var routedDistricts)
                    && routedDistricts.ToString().Contains(districtName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(submission => submission.ReceivedOnUtc)
                .ToList();

            return Task.FromResult<IReadOnlyList<NormalizedInterestFormSubmission>>(results);
        }
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByReceivedRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var results = submissions
                .Where(submission => submission.ReceivedOnUtc >= startUtc && submission.ReceivedOnUtc < endUtc)
                .OrderBy(submission => submission.ReceivedOnUtc)
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
            var results = districtContacts
                .Where(contact => IsEffective(contact.EffectiveFromUtc, contact.EffectiveToUtc, contact.IsActive, asOfUtc))
                .GroupBy(contact => contact.DistrictName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(contact => contact.Version)
                    .ThenByDescending(contact => contact.EffectiveFromUtc)
                    .ThenByDescending(contact => contact.Id)
                    .First())
                .OrderBy(contact => contact.DistrictName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<ContactsForDistrict>>(results);
        }
    }

    public async Task<ContactsForDistrict?> GetEffectiveDistrictContactByNameAsync(
        string districtName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        return contacts.FirstOrDefault(contact => string.Equals(contact.DistrictName, districtName, StringComparison.OrdinalIgnoreCase));
    }

    public Task<ContactsForCountry?> GetEffectiveCountryContactAsync(
        string normalizedCountryName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var result = countryContacts
                .Where(contact => IsEffective(contact.EffectiveFromUtc, contact.EffectiveToUtc, contact.IsActive, asOfUtc))
                .Where(contact => string.Equals(contact.CountryName, normalizedCountryName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(contact => contact.Version)
                .ThenByDescending(contact => contact.EffectiveFromUtc)
                .ThenByDescending(contact => contact.Id)
                .FirstOrDefault();

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

    private static bool IsEffective(DateTimeOffset fromUtc, DateTimeOffset? toUtc, bool isActive, DateTimeOffset asOfUtc)
    {
        return isActive && fromUtc <= asOfUtc && (toUtc is null || toUtc > asOfUtc);
    }
}
