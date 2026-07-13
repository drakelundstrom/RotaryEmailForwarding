using System.Globalization;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailTemplateService(AppConfiguration configuration)
{
    public IReadOnlyList<OutboundEmailMessage> BuildMessages(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var message = route.Kind switch
        {
            SubmissionRouteKind.District => BuildDistrictForwardingMessage(submission, route),
            SubmissionRouteKind.Country => BuildCountryForwardingMessage(submission, route),
            SubmissionRouteKind.UncertifiedCountry => BuildManualRoutingMessage(submission, route),
            _ => BuildManualRoutingMessage(submission, route)
        };

        return [message];
    }

    public OutboundEmailMessage BuildOperatorFailureMessage(
        string correlationId,
        string failureSummary,
        string rawSubmissionJson)
    {
        return new OutboundEmailMessage(
            $"operator-failure:{correlationId}",
            OutboundEmailMessageType.OperatorFailure,
            [configuration.OperatorEmail],
            "Failure to process submission or send email",
            $"Correlation ID: {correlationId}{Environment.NewLine}{Environment.NewLine}{failureSummary}{Environment.NewLine}{Environment.NewLine}{rawSubmissionJson}");
    }

    public static IReadOnlyList<string> BuildInterestedPartyRecipients(NormalizedInterestFormSubmission submission)
    {
        var recipients = new List<string>();
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);

        if (submitterType == InterestFormSubmitterType.Student)
        {
            AddEmail(recipients, submission.StudentEmail);
            AddEmail(recipients, submission.ParentEmail);
            return recipients
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        AddEmail(recipients, submission.ContactEmail);
        return recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private OutboundEmailMessage BuildDistrictForwardingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            route.DistrictContacts.SelectMany(contact => contact.EmailAddresses),
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        var bodyIntro = route.HasMultipleDistrictMatches
            ? BuildMultipleDistrictIntro(route)
            : $"We are reaching out to connect you with our local coordinators in {FormatDistrictForSentence(route.DistrictContacts.FirstOrDefault()?.District)}.";

        return new OutboundEmailMessage(
            $"district:{submission.Id}",
            OutboundEmailMessageType.DistrictRepresentative,
            recipients,
            $"Rotary Youth Exchange interest from {UnknownIfBlank(submission.Name)}",
            BuildSharedBody(bodyIntro, submission));
    }

    private OutboundEmailMessage BuildCountryForwardingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            route.CountryContact?.EmailAddresses ?? [],
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        var country = route.CountryContact?.Country ?? submission.CountryOfResidence;

        return new OutboundEmailMessage(
            $"country:{submission.Id}",
            OutboundEmailMessageType.CountryRepresentative,
            recipients,
            $"Rotary Youth Exchange interest from {UnknownIfBlank(submission.Name)}",
            BuildSharedBody(
                $"We are reaching out to connect you with our local coordinators for {GetDisplayCountryName(country)}.",
                submission));
    }

    private OutboundEmailMessage BuildManualRoutingMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = BuildRecipients(
            [configuration.OperatorEmail],
            BuildInterestedPartyRecipients(submission),
            ShouldCopySupport(submission) ? [configuration.SupportEmail] : []);

        return new OutboundEmailMessage(
            $"operator-fallback:{submission.Id}",
            OutboundEmailMessageType.OperatorFallback,
            recipients,
            "Rotary Youth Exchange interest needs routing review",
            BuildSharedBody(
                "Our automated system was not able to resolve where you should be forwarded, but an admin will take a look and should have this resolved in a week or less. In the meantime, feel free to reach out to your local Rotary club!",
                submission,
                route.Errors));
    }

    private bool ShouldCopySupport(NormalizedInterestFormSubmission submission)
    {
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);
        return submitterType is InterestFormSubmitterType.Rotarian or InterestFormSubmitterType.Other;
    }

    private static IReadOnlyList<string> BuildRecipients(params IEnumerable<string?>[] recipientGroups)
    {
        var recipients = new List<string>();
        foreach (var group in recipientGroups)
        {
            foreach (var email in group)
            {
                AddEmail(recipients, email);
            }
        }

        return recipients
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddEmail(List<string> recipients, string? email)
    {
        if (EmailAddressUtility.IsUsable(email))
        {
            recipients.Add(email!.Trim());
        }
    }

    private static string BuildMultipleDistrictIntro(SubmissionRoute route)
    {
        var districtCount = route.DistrictContacts.Count;
        var districtNames = string.Join(", ", route.DistrictContacts.Select(contact => contact.District));

        var included = districtCount == 2 ? "both" : "all matching districts";
        return $"You are on the border of {districtCount.ToString(CultureInfo.InvariantCulture)} Rotary districts, so we have included {included} to make sure the right person gets in contact.{Environment.NewLine}Districts included: {districtNames}.";
    }

    private static string FormatDistrictForSentence(string? district)
    {
        var trimmed = UnknownIfBlank(district);
        return trimmed.StartsWith("district ", StringComparison.OrdinalIgnoreCase)
            ? $"Rotary {trimmed}"
            : $"Rotary District {trimmed}";
    }

    private static string BuildSharedBody(
        string intro,
        NormalizedInterestFormSubmission submission,
        IReadOnlyList<string>? routingErrors = null)
    {
        var sections = new List<string>
        {
            "Hello,",
            string.Empty,
            intro,
            string.Empty,
            "Here are the details from the interest form:",
            SubmissionInformationBlock(submission)
        };

        if (routingErrors?.Count > 0)
        {
            sections.Add(string.Empty);
            sections.Add($"Routing notes: {string.Join("; ", routingErrors)}");
        }

        return string.Join(Environment.NewLine, sections);
    }

    private static string SubmissionInformationBlock(NormalizedInterestFormSubmission submission)
    {
        var lines = new List<string>
        {
            $"Who submitted: {UnknownIfBlank(submission.SubmissionType)}",
            $"Name: {UnknownIfBlank(submission.Name)}",
            $"Student age: {UnknownIfBlank(submission.Age)}"
        };

        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);
        if (submitterType == InterestFormSubmitterType.Student)
        {
            lines.Add($"Student email: {UnknownIfBlank(submission.StudentEmail)}");
            lines.Add($"Student phone: {UnknownIfBlank(submission.StudentPhone)}");
            lines.Add($"Parent email: {UnknownIfBlank(submission.ParentEmail)}");
            lines.Add($"Parent phone: {UnknownIfBlank(submission.ParentPhone)}");
        }
        else
        {
            lines.Add($"Contact email: {UnknownIfBlank(submission.ContactEmail)}");
            lines.Add($"Contact phone: {UnknownIfBlank(submission.ContactPhone)}");
        }

        lines.AddRange(
        [
            $"Country of residence: {GetDisplayCountryName(submission.CountryOfResidence)}",
            $"State: {UnknownIfBlank(submission.State)}",
            $"City: {UnknownIfBlank(submission.City)}",
            $"Zipcode/postal code: {UnknownIfBlank(submission.Zipcode)}",
            $"Question: {UnknownIfBlank(submission.OptionalSubmissionQuestion)}"
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    private static string GetDisplayCountryName(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return "Unknown";
        }

        var normalized = SubmissionNormalizer.NormalizeCountry(country);
        return normalized switch
        {
            "usa" => "USA",
            "canada" => "Canada",
            "mexico" => "Mexico",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(country.Trim().ToLowerInvariant())
        };
    }

    private static string UnknownIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }
}
