using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;

namespace RotaryEmailForwarding.FunctionApp.Routing;

public sealed class SubmissionRoutingService(IApplicationRepository repository, IClock clock)
{
    public async Task<SubmissionRoute> RouteAsync(
        NormalizedInterestFormSubmission submission,
        CancellationToken cancellationToken)
    {
        var country = SubmissionNormalizer.NormalizeCountry(submission.CountryOfResidence);
        if (country is null)
        {
            return Fallback("Country of residence missing");
        }

        if (country is "usa" or "canada")
        {
            if (string.IsNullOrWhiteSpace(submission.Zipcode))
            {
                return Fallback("Zipcode missing for district routing");
            }

            var contacts = await repository.GetEffectiveDistrictContactsAsync(clock.UtcNow, cancellationToken);
            var matches = contacts
                .Where(contact => contact.Zipcodes.Any(zip => string.Equals(
                    SubmissionNormalizer.NormalizeZipcode(zip, country),
                    submission.Zipcode,
                    StringComparison.OrdinalIgnoreCase)))
                .ToList();

            return matches.Count == 0
                ? Fallback("No district contact found for zipcode")
                : new SubmissionRoute
                {
                    Kind = SubmissionRouteKind.District,
                    DistrictContacts = matches,
                    Errors = matches.Count > 1
                        ? ["Multiple districts found for zipcode"]
                        : []
                };
        }

        var countryContact = await repository.GetEffectiveCountryContactAsync(country, clock.UtcNow, cancellationToken);
        if (countryContact is null)
        {
            return Fallback("No country contact found");
        }

        return countryContact.IsCertified
            ? new SubmissionRoute
            {
                Kind = SubmissionRouteKind.Country,
                CountryContact = countryContact
            }
            : new SubmissionRoute
            {
                Kind = SubmissionRouteKind.UncertifiedCountry,
                CountryContact = countryContact,
                Errors = ["Country is not certified for the program"]
            };
    }

    private static SubmissionRoute Fallback(string error)
    {
        return new SubmissionRoute
        {
            Kind = SubmissionRouteKind.Fallback,
            Errors = [error]
        };
    }
}
