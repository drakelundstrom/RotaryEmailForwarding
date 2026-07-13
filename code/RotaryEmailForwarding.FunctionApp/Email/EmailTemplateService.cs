using System.Globalization;
using System.Net;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailTemplateService(AppConfiguration configuration)
{
    private const string PublicSiteUrl = "https://studyabroadscholarships.org/";
    private const string PublicSiteDisplayName = "studyabroadscholarships.org";

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

        AddEmail(recipients, submission.StudentEmail);
        AddEmail(recipients, submission.ParentEmail);

        if (submitterType == InterestFormSubmitterType.Student)
        {
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

        return new OutboundEmailMessage(
            $"district:{submission.Id}",
            OutboundEmailMessageType.DistrictRepresentative,
            recipients,
            $"Rotary Youth Exchange interest from {UnknownIfBlank(submission.Name)}",
            BuildSharedBody(
                BuildDistrictGreeting(route),
                BuildDistrictIntro(route),
                submission,
                "your district"),
            true);
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
                $"Hello RYE {Html(GetDisplayCountryName(country))} Representatives,",
                [
                    $"An interested person in your country has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at {SiteLink()}.",
                    "They have been told to expect a follow up within 2 weeks."
                ],
                submission,
                "your country"),
            true);
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
                "Hello,",
                [
                    $"An interested person has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at {SiteLink()}.",
                    "The automated system was not able to resolve where this submission should be forwarded, so an admin should review it.",
                    "The submitter should expect a follow up within 2 weeks."
                ],
                submission,
                "this submission",
                route.Errors),
            true);
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

    private static string BuildDistrictGreeting(SubmissionRoute route)
    {
        var districtNames = route.DistrictContacts
            .Select(contact => FormatDistrictForGreeting(contact.District))
            .ToList();

        return districtNames.Count == 0
            ? "Hello RYE District Representatives,"
            : $"Hello RYE {Html(JoinForSentence(districtNames))} Representatives,";
    }

    private static IReadOnlyList<string> BuildDistrictIntro(SubmissionRoute route)
    {
        if (!route.HasMultipleDistrictMatches)
        {
            return
            [
                $"An interested person in your district has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at {SiteLink()}.",
                "They have been informed of the relevant Rotary district and told to expect a follow up within 2 weeks."
            ];
        }

        var districtNames = JoinForSentence(route.DistrictContacts.Select(contact => FormatDistrictForGreeting(contact.District)));
        return
        [
            $"An interested person has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at {SiteLink()}.",
            $"This submission matched multiple Rotary districts ({Html(districtNames)}), so all matching districts have been included.",
            "The submitter should expect a follow up within 2 weeks."
        ];
    }

    private static string FormatDistrictForGreeting(string? district)
    {
        var trimmed = UnknownIfBlank(district);
        return trimmed.StartsWith("district ", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"District {trimmed}";
    }

    private string BuildSharedBody(
        string greeting,
        IReadOnlyList<string> introParagraphs,
        NormalizedInterestFormSubmission submission,
        string supportContext,
        IReadOnlyList<string>? routingErrors = null)
    {
        var sections = new List<string>
        {
            Paragraph(greeting)
        };

        sections.AddRange(introParagraphs.Select(Paragraph));
        sections.AddRange(
        [
            Paragraph("Here is the information from the form submission:"),
            SubmissionInformationBlock(submission)
        ]);

        if (routingErrors?.Count > 0)
        {
            sections.Add(Paragraph($"Routing notes: {Html(string.Join("; ", routingErrors))}"));
        }

        sections.Add(Paragraph($"If you have any admin support questions, advice for the process, need to add or remove email addresses for {Html(supportContext)}, or want a list of previous submissions, please reach out to {SupportEmailLink()}."));
        sections.Add(Paragraph($"Thank you for your support of {SiteLink()}!"));

        return string.Join(Environment.NewLine, sections);
    }

    private static string SubmissionInformationBlock(NormalizedInterestFormSubmission submission)
    {
        var lines = new List<string>();
        AddLine(lines, "Who are you?", submission.SubmissionType);
        AddLine(lines, "Name", submission.Name);
        AddLine(lines, "Current age (years)", submission.Age);
        AddLine(lines, "Current age of your student (years)", submission.ParentEnteredAge);
        AddLine(lines, "Student's email", submission.StudentEmail);
        AddLine(lines, "Student's phone number", submission.StudentPhone);
        AddLine(lines, "Parent's email", submission.ParentEmail);
        AddLine(lines, "Parent's phone number", submission.ParentPhone);
        AddLine(lines, "Contact email", submission.ContactEmail);
        AddLine(lines, "Contact phone number", submission.ContactPhone);
        AddCountryLine(lines, submission.CountryOfResidence);
        AddLine(lines, "State or province", submission.State);
        AddLine(lines, "City", submission.City);
        AddLine(lines, "Zip code or first 3 of CDN postal code", submission.Zipcode);
        AddLine(lines, "Question", submission.OptionalSubmissionQuestion);

        return lines.Count == 0
            ? Paragraph("No form details were provided.")
            : $"<p>{string.Join("<br>", lines)}</p>";
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

    private static void AddLine(List<string> lines, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"<strong>{label}:</strong> {Html(value.Trim())}");
        }
    }

    private static void AddCountryLine(List<string> lines, string? country)
    {
        if (!string.IsNullOrWhiteSpace(country))
        {
            lines.Add($"<strong>Country of residence:</strong> {Html(GetDisplayCountryName(country))}");
        }
    }

    private static string Paragraph(string text)
    {
        return $"<p>{text}</p>";
    }

    private static string SiteLink()
    {
        return $"""<a href="{PublicSiteUrl}">{PublicSiteDisplayName}</a>""";
    }

    private string SupportEmailLink()
    {
        var operatorEmail = Html(configuration.OperatorEmail);
        return $"""<a href="mailto:{operatorEmail}">{operatorEmail}</a>""";
    }

    private static string JoinForSentence(IEnumerable<string> values)
    {
        var items = values.ToList();
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}"
        };
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
