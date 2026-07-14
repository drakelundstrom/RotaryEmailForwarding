using Microsoft.Azure.Cosmos;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

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

    public async Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByDistrictAsync(
        string districtName,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var district = await GetEffectiveDistrictContactByNameAsync(districtName, DateTimeOffset.UtcNow, cancellationToken);
        if (district is null || district.ZipCodes.Count == 0)
        {
            return [];
        }

        var normalizedCountry = SubmissionNormalizer.NormalizeCountry(district.Country);
        if (normalizedCountry is null)
        {
            return [];
        }

        var normalizedZipCodes = district.ZipCodes
            .Select(zip => SubmissionNormalizer.NormalizeZipcode(zip, normalizedCountry))
            .Where(zip => !string.IsNullOrWhiteSpace(zip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedZipCodes.Count == 0)
        {
            return [];
        }

        var zipParameters = normalizedZipCodes
            .Select((_, index) => $"@zip{index}")
            .ToList();
        var query = $"""
            SELECT * FROM c
            WHERE c.Type = @type
              AND c.ReceivedOnUtc >= @sinceUtc
              AND c.CountryOfResidence = @country
              AND c.Zipcode IN ({string.Join(",", zipParameters)})
            ORDER BY c.ReceivedOnUtc DESC
            """;

        var queryDefinition = new QueryDefinition(query)
            .WithParameter("@type", SubmissionType)
            .WithParameter("@sinceUtc", sinceUtc)
            .WithParameter("@country", normalizedCountry);

        for (var index = 0; index < normalizedZipCodes.Count; index++)
        {
            queryDefinition = queryDefinition.WithParameter(zipParameters[index], normalizedZipCodes[index]);
        }

        return await QueryAsync<NormalizedInterestFormSubmission>(
            queryDefinition,
            cancellationToken);
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByStorageTimestampRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND c._ts >= @startEpochSeconds
              AND c._ts < @endEpochSeconds
            ORDER BY c._ts ASC
            """;

        return QueryAsync<NormalizedInterestFormSubmission>(
            new QueryDefinition(query)
                .WithParameter("@type", SubmissionType)
                .WithParameter("@startEpochSeconds", startUtc.ToUnixTimeSeconds())
                .WithParameter("@endEpochSeconds", endUtc.ToUnixTimeSeconds()),
            cancellationToken);
    }

    public Task<IReadOnlyList<NormalizedInterestFormSubmission>> GetSubmissionsByReceivedOnOrStorageTimestampRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
              AND (
                (
                  IS_DEFINED(c.ReceivedOnUtc)
                  AND NOT IS_NULL(c.ReceivedOnUtc)
                  AND c.ReceivedOnUtc != @defaultReceivedOnUtc
                  AND c.ReceivedOnUtc >= @startUtc
                  AND c.ReceivedOnUtc < @endUtc
                )
                OR (
                  (NOT IS_DEFINED(c.ReceivedOnUtc) OR IS_NULL(c.ReceivedOnUtc) OR c.ReceivedOnUtc = @defaultReceivedOnUtc)
                  AND c._ts >= @startEpochSeconds
                  AND c._ts < @endEpochSeconds
                )
              )
            ORDER BY c._ts ASC
            """;

        return QueryAsync<NormalizedInterestFormSubmission>(
            new QueryDefinition(query)
                .WithParameter("@type", SubmissionType)
                .WithParameter("@defaultReceivedOnUtc", default(DateTimeOffset))
                .WithParameter("@startUtc", startUtc)
                .WithParameter("@endUtc", endUtc)
                .WithParameter("@startEpochSeconds", startUtc.ToUnixTimeSeconds())
                .WithParameter("@endEpochSeconds", endUtc.ToUnixTimeSeconds()),
            cancellationToken);
    }

    public Task<IReadOnlyList<ContactsForDistrict>> GetEffectiveDistrictContactsAsync(
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT * FROM c
            WHERE c.Type = @type
            ORDER BY c._ts DESC
            """;

        return EffectiveDistrictsAsync(query, cancellationToken);
    }

    public async Task<ContactsForDistrict?> GetEffectiveDistrictContactByNameAsync(
        string districtName,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var contacts = await GetEffectiveDistrictContactsAsync(asOfUtc, cancellationToken);
        return contacts.FirstOrDefault(contact => string.Equals(contact.District, districtName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ContactsForCountry?> GetEffectiveCountryContactAsync(
        string normalizedCountry,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 * FROM c
            WHERE c.Type = @type
              AND LOWER(c.Country) = LOWER(@countryName)
            ORDER BY c._ts DESC
            """;

        var contacts = await QueryAsync<ContactsForCountry>(
            new QueryDefinition(query)
                .WithParameter("@type", CountryType)
                .WithParameter("@countryName", normalizedCountry),
            cancellationToken);

        return contacts.FirstOrDefault();
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
        CancellationToken cancellationToken)
    {
        var contacts = await QueryAsync<ContactsForDistrict>(
            new QueryDefinition(query)
                .WithParameter("@type", DistrictType),
            cancellationToken);

        return contacts
            .GroupBy(contact => $"{SubmissionNormalizer.NormalizeCountry(contact.Country)}:{contact.District}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(contact => contact.Country, StringComparer.OrdinalIgnoreCase)
            .ThenBy(contact => contact.District, StringComparer.OrdinalIgnoreCase)
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
