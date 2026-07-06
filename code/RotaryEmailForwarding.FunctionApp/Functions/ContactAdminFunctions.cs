using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using RotaryEmailForwarding.FunctionApp.Authorization;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class ContactAdminFunctions(
    IApplicationRepository repository,
    AdminAuthorizationService authorizationService,
    IClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("CreateContactsForDistricts")]
    public async Task<HttpResponseData> CreateDistricts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "contacts-for-districts")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return await ErrorAsync(request, HttpStatusCode.Unauthorized, "Admin authorization is required.");
        }

        List<ContactsForDistrict> contacts;
        try
        {
            contacts = await JsonSerializer.DeserializeAsync<List<ContactsForDistrict>>(request.Body, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "The request body must be valid district contact JSON.");
        }

        var validationErrors = ValidateDistrictContacts(contacts);
        if (validationErrors.Count > 0)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, string.Join("; ", validationErrors));
        }

        var effectiveContacts = contacts
            .Select(contact => contact with
            {
                Id = string.IsNullOrWhiteSpace(contact.Id) ? Guid.NewGuid().ToString("D") : contact.Id,
                Country = SubmissionNormalizer.NormalizeCountry(contact.Country) ?? contact.Country
            })
            .ToList();

        await repository.UpsertDistrictContactsAsync(effectiveContacts, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { count = effectiveContacts.Count }, cancellationToken);
        return response;
    }

    [Function("CreateContactsForCountries")]
    public async Task<HttpResponseData> CreateCountries(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "contacts-for-countries")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return await ErrorAsync(request, HttpStatusCode.Unauthorized, "Admin authorization is required.");
        }

        List<ContactsForCountry> contacts;
        try
        {
            contacts = await JsonSerializer.DeserializeAsync<List<ContactsForCountry>>(request.Body, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, "The request body must be valid country contact JSON.");
        }

        var validationErrors = ValidateCountryContacts(contacts);
        if (validationErrors.Count > 0)
        {
            return await ErrorAsync(request, HttpStatusCode.BadRequest, string.Join("; ", validationErrors));
        }

        var effectiveContacts = contacts
            .Select(contact => contact with
            {
                Id = string.IsNullOrWhiteSpace(contact.Id) ? Guid.NewGuid().ToString("D") : contact.Id,
                Country = SubmissionNormalizer.NormalizeCountry(contact.Country) ?? contact.Country
            })
            .ToList();

        await repository.UpsertCountryContactsAsync(effectiveContacts, cancellationToken);
        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { count = effectiveContacts.Count }, cancellationToken);
        return response;
    }

    [Function("GetContactsForDistrict")]
    public async Task<HttpResponseData> GetDistrict(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "contacts-for-districts/{districtName}")] HttpRequestData request,
        string districtName,
        CancellationToken cancellationToken)
    {
        if (!authorizationService.IsAuthorized(request))
        {
            return await ErrorAsync(request, HttpStatusCode.Unauthorized, "Admin authorization is required.");
        }

        var contact = await repository.GetEffectiveDistrictContactByNameAsync(districtName, clock.UtcNow, cancellationToken);
        if (contact is null)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(contact, cancellationToken);
        return response;
    }

    private static IReadOnlyList<string> ValidateDistrictContacts(IReadOnlyList<ContactsForDistrict> contacts)
    {
        var errors = new List<string>();
        if (contacts.Count == 0)
        {
            errors.Add("At least one district contact is required.");
        }

        foreach (var contact in contacts)
        {
            if (string.IsNullOrWhiteSpace(contact.Country))
            {
                errors.Add("Country is required.");
            }

            if (string.IsNullOrWhiteSpace(contact.District))
            {
                errors.Add("District is required.");
            }

            if (contact.EmailAddresses.Count == 0)
            {
                errors.Add($"District {contact.District} must include at least one email address.");
            }

            if (contact.ZipCodes.Count == 0)
            {
                errors.Add($"District {contact.District} must include at least one zipcode.");
            }
        }

        var duplicates = contacts
            .GroupBy(contact => $"{contact.Country}:{contact.District}", StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        errors.AddRange(duplicates.Select(duplicate => $"Duplicate district entry: {duplicate}."));

        return errors;
    }

    private static IReadOnlyList<string> ValidateCountryContacts(IReadOnlyList<ContactsForCountry> contacts)
    {
        var errors = new List<string>();
        if (contacts.Count == 0)
        {
            errors.Add("At least one country contact is required.");
        }

        foreach (var contact in contacts)
        {
            if (string.IsNullOrWhiteSpace(contact.Country))
            {
                errors.Add("Country is required.");
            }

            if (contact.IsCertified && contact.EmailAddresses.Count == 0)
            {
                errors.Add($"Certified country {contact.Country} must include at least one email address.");
            }
        }

        var duplicates = contacts
            .GroupBy(contact => contact.Country, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        errors.AddRange(duplicates.Select(duplicate => $"Duplicate country entry: {duplicate}."));

        return errors;
    }

    private static async Task<HttpResponseData> ErrorAsync(HttpRequestData request, HttpStatusCode status, string message)
    {
        var response = request.CreateResponse(status);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }
}
