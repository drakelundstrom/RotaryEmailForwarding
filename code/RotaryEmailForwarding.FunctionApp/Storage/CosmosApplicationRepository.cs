using Microsoft.Azure.Cosmos;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;

namespace RotaryEmailForwarding.FunctionApp.Storage;

public sealed class CosmosApplicationRepository : IApplicationRepository
{
    private const string SubmissionType = "InterestFormSubmission";
    private const string RequestLogType = "RequestBodyLog";
    private const string DistrictType = "ContactsForDistrict";
    private const string CountryType = "ContactsForCountry";

    private readonly Container container;

    public CosmosApplicationRepository(AppConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.DatabaseConnectionString))
        {
            throw new InvalidOperationException("databaseConnectionString is required for Cosmos storage.");
        }

        var client = new CosmosClient(configuration.DatabaseConnectionString);
        container = client.GetContainer(configuration.CosmosDatabaseName, configuration.CosmosContainerName);
    }

    public Task StoreRawRequestAsync(RequestBodyLog requestLog, CancellationToken cancellationToken)
    {
        return CreateItemAsync(requestLog, RequestLogType, cancellationToken);
    }

    public Task InsertSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken)
    {
        return CreateItemAsync(submission, SubmissionType, cancellationToken);
    }

    public Task UpdateSubmissionAsync(NormalizedInterestFormSubmission submission, CancellationToken cancellationToken)
    {
        return container.UpsertItemAsync(submission, Partition(SubmissionType), cancellationToken: cancellationToken);
    }

    public async Task<NormalizedInterestFormSubmission?> GetSubmissionAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await container.ReadItemAsync<NormalizedInterestFormSubmission>(
                id,
                Partition(SubmissionType),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetRetryableUnsentSubmissionsAsync(
        DateTimeOffset retryWindowStartUtc,
        DateTimeOffset retryWindowEndUtc,
        DateTimeOffset nowUtc,
        int maxCount,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP @maxCount * FROM c
            WHERE c.Type = @type
              AND (NOT IS_DEFINED(c.SentOnUtc) OR IS_NULL(c.SentOnUtc))
              AND (c.EmailDeliveryStatus = 0 OR c.EmailDeliveryStatus = 2 OR c.EmailDeliveryStatus = "Pending" OR c.EmailDeliveryStatus = "RetryPending")
              AND c.ReceivedOnUtc < @retryWindowEndUtc
              AND (NOT IS_DEFINED(c.NextEmailAttemptOnUtc) OR IS_NULL(c.NextEmailAttemptOnUtc) OR c.NextEmailAttemptOnUtc <= @nowUtc)
            ORDER BY c.ReceivedOnUtc ASC
            """;

        return QueryAsync<NormalizedInterestFormSubmission>(
            new QueryDefinition(query)
                .WithParameter("@maxCount", maxCount)
                .WithParameter("@type", SubmissionType)
                .WithParameter("@retryWindowEndUtc", retryWindowEndUtc)
                .WithParameter("@nowUtc", nowUtc),
            cancellationToken);
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByDistrictAsync(
        string districtName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND c.ReceivedOnUtc >= @sinceUtc
              AND CONTAINS(LOWER(TO_STRING(c.RoutedDistricts)), LOWER(@districtName))
            ORDER BY c.ReceivedOnUtc DESC
            """;

        return QueryAsync<NormalizedInterestFormSubmission>(
            new QueryDefinition(query)
                .WithParameter("@type", SubmissionType)
                .WithParameter("@sinceUtc", sinceUtc)
                .WithParameter("@districtName", districtName),
            cancellationToken);
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByReceivedRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND c.ReceivedOnUtc >= @startUtc
              AND c.ReceivedOnUtc < @endUtc
            ORDER BY c.ReceivedOnUtc ASC
            """;

        return QueryAsync<NormalizedInterestFormSubmission>(
            new QueryDefinition(query)
                .WithParameter("@type", SubmissionType)
                .WithParameter("@startUtc", startUtc)
                .WithParameter("@endUtc", endUtc),
            cancellationToken);
    }

    public Task<IReadOnlyList<ContactsForDistrict>> GetEffectiveDistrictContactsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND c.IsActive = true
              AND c.EffectiveFromUtc <= @asOfUtc
              AND (NOT IS_DEFINED(c.EffectiveToUtc) OR IS_NULL(c.EffectiveToUtc) OR c.EffectiveToUtc > @asOfUtc)
            """;

        return EffectiveDistrictsAsync(query, asOfUtc, cancellationToken);
    }

    public async Task<ContactsForDistrict?> GetEffectiveDistrictContactByNameAsync(
        string districtName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        return contacts.FirstOrDefault(contact => string.Equals(contact.DistrictName, districtName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ContactsForCountry?> GetEffectiveCountryContactAsync(
        string normalizedCountryName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND LOWER(c.CountryName) = LOWER(@countryName)
              AND c.IsActive = true
              AND c.EffectiveFromUtc <= @asOfUtc
              AND (NOT IS_DEFINED(c.EffectiveToUtc) OR IS_NULL(c.EffectiveToUtc) OR c.EffectiveToUtc > @asOfUtc)
            """;

        var contacts = await QueryAsync<ContactsForCountry>(
            new QueryDefinition(query)
                .WithParameter("@type", CountryType)
                .WithParameter("@countryName", normalizedCountryName)
                .WithParameter("@asOfUtc", asOfUtc),
            cancellationToken);

        return contacts
            .OrderByDescending(contact => contact.Version)
            .ThenByDescending(contact => contact.EffectiveFromUtc)
            .ThenByDescending(contact => contact.Id)
            .FirstOrDefault();
    }

    public async Task UpsertDistrictContactsAsync(
        IReadOnlyList<ContactsForDistrict> contacts,
        CancellationToken cancellationToken)
    {
        foreach (var contact in contacts)
        {
            await container.UpsertItemAsync(contact, Partition(DistrictType), cancellationToken: cancellationToken);
        }
    }

    public async Task UpsertCountryContactsAsync(
        IReadOnlyList<ContactsForCountry> contacts,
        CancellationToken cancellationToken)
    {
        foreach (var contact in contacts)
        {
            await container.UpsertItemAsync(contact, Partition(CountryType), cancellationToken: cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ContactsForDistrict>> EffectiveDistrictsAsync(
        string query,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await QueryAsync<ContactsForDistrict>(
            new QueryDefinition(query)
                .WithParameter("@type", DistrictType)
                .WithParameter("@asOfUtc", asOfUtc),
            cancellationToken);

        return contacts
            .GroupBy(contact => contact.DistrictName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(contact => contact.Version)
                .ThenByDescending(contact => contact.EffectiveFromUtc)
                .ThenByDescending(contact => contact.Id)
                .First())
            .OrderBy(contact => contact.DistrictName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        QueryDefinition queryDefinition,
        CancellationToken cancellationToken)
    {
        using var iterator = container.GetItemQueryIterator<T>(queryDefinition);
        var results = new List<T>();

        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(page);
        }

        return results;
    }

    private async Task CreateItemAsync<T>(T item, string type, CancellationToken cancellationToken)
    {
        await container.CreateItemAsync(item, Partition(type), cancellationToken: cancellationToken);
    }

    private static PartitionKey Partition(string type)
    {
        return new PartitionKey(type);
    }
}
