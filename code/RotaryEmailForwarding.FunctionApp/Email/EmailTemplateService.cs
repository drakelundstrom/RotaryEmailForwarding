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

    public OutboundEmailMessage BuildMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        return route.Kind switch
        {
            SubmissionRouteKind.District => BuildDistrictForwardingMessage(submission, route),
            SubmissionRouteKind.Country => BuildCountryForwardingMessage(submission, route),
            SubmissionRouteKind.UncertifiedCountry => BuildManualRoutingMessage(submission, route),
            _ => BuildManualRoutingMessage(submission, route)
        };
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
            BuildForwardingSubject(submission),
            BuildSharedBody(
                BuildSubmitterGreeting(submission),
                BuildDistrictIntro(submission, route),
                submission,
                "your district",
                BuildRecipientSectionLabel(submission, isManualRouting: false)),
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
            BuildForwardingSubject(submission),
            BuildSharedBody(
                BuildSubmitterGreeting(submission),
                BuildCountryIntro(submission, country),
                submission,
                "your country",
                BuildRecipientSectionLabel(submission, isManualRouting: false)),
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
                BuildSubmitterGreeting(submission),
                BuildManualRoutingIntro(submission),
                submission,
                "this submission",
                BuildRecipientSectionLabel(submission, isManualRouting: true),
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

    private static string BuildSubmitterGreeting(NormalizedInterestFormSubmission submission)
    {
        if (IsRotarianSubmission(submission))
        {
            return "Hello fellow Rotarian,";
        }

        return string.IsNullOrWhiteSpace(submission.Name)
            ? "Hello,"
            : $"Hello {Html(submission.Name.Trim())},";
    }

    private static string BuildForwardingSubject(NormalizedInterestFormSubmission submission)
    {
        var purpose = IsRotarianSubmission(submission) ? "question" : "interest";
        return $"Rotary Youth Exchange {purpose} from {UnknownIfBlank(submission.Name)}";
    }

    private static IReadOnlyList<string> BuildDistrictIntro(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        if (!route.HasMultipleDistrictMatches)
        {
            var districtName = route.DistrictContacts.Count == 0
                ? "your local district"
                : FormatDistrictForGreeting(route.DistrictContacts[0].District);

            if (IsRotarianSubmission(submission))
            {
                return
                [
                    "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.",
                    $"The Rotary Youth Exchange representatives from {Html(districtName)} and our support team have been added to this email.",
                    "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.",
                    "They should reply within 2 weeks with guidance specific to your area."
                ];
            }

            return
            [
                "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.",
                $"Your local Rotary Youth Exchange representatives from {Html(districtName)} have been added to this email. They should reply within 2 weeks with information about how the program works in your area.",
                "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions."
            ];
        }

        var districtNames = JoinForSentence(route.DistrictContacts.Select(contact => FormatDistrictForGreeting(contact.District)));
        if (IsRotarianSubmission(submission))
        {
            return
            [
                "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.",
                $"Your location matched multiple Rotary districts ({Html(districtNames)}), so representatives from each district and our support team have been added to this email.",
                "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.",
                "They should reply within 2 weeks with guidance specific to your area."
            ];
        }

        return
        [
            "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.",
            $"Your location matched multiple Rotary districts ({Html(districtNames)}), so representatives from each district have been added to this email.",
            "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.",
            "They should reply within 2 weeks with information about how the program works in your area."
        ];
    }

    private static IReadOnlyList<string> BuildCountryIntro(
        NormalizedInterestFormSubmission submission,
        string? country)
    {
        var displayCountry = Html(GetDisplayCountryName(country));
        if (IsRotarianSubmission(submission))
        {
            return
            [
                "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.",
                $"The Rotary Youth Exchange representatives for {displayCountry} and our support team have been added to this email.",
                "To keep every representative and the support team included, choose <strong>&ldquo;Reply all&rdquo;</strong> when sending additional details or questions.",
                "They should reply within 2 weeks with guidance specific to your area."
            ];
        }

        return
        [
            "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.",
            $"The Rotary Youth Exchange representatives for {displayCountry} have been added to this email.",
            "To make sure everyone sees your message, choose <strong>&ldquo;Reply all&rdquo;</strong> when you ask your questions.",
            "They should reply within 2 weeks with information about how the program works in your area."
        ];
    }

    private static IReadOnlyList<string> BuildManualRoutingIntro(NormalizedInterestFormSubmission submission)
    {
        if (IsRotarianSubmission(submission))
        {
            return
            [
                "Thank you for participating in Rotary Youth Exchange and for reaching out with your question about the Study Abroad Scholarships offered as part of the program.",
                "We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin and support teams have been added to this email to review your request.",
                "They should reply within 2 weeks with guidance about the next steps."
            ];
        }

        return
        [
            "Thank you for reaching out to learn more about the Study Abroad Scholarships offered as part of Rotary Youth Exchange.",
            "We could not automatically identify the Rotary Youth Exchange representatives for your area, so our admin team has been added to this email to review your request.",
            "The admin team should reply within 2 weeks with information about the next steps."
        ];
    }

    private static bool IsRotarianSubmission(NormalizedInterestFormSubmission submission)
    {
        return SubmissionNormalizer.GetSubmitterType(submission.SubmissionType) == InterestFormSubmitterType.Rotarian;
    }

    private static string BuildSubmitterSectionLabel(NormalizedInterestFormSubmission submission)
    {
        return SubmissionNormalizer.GetSubmitterType(submission.SubmissionType) switch
        {
            InterestFormSubmitterType.Student => "For the submitting student:",
            InterestFormSubmitterType.Parent => "For the submitting family:",
            InterestFormSubmitterType.Rotarian => "For the submitting Rotarian:",
            _ => "For the submitter:"
        };
    }

    private static string BuildRecipientSectionLabel(
        NormalizedInterestFormSubmission submission,
        bool isManualRouting)
    {
        if (isManualRouting)
        {
            return IsRotarianSubmission(submission)
                ? "For the Rotary admin and support teams:"
                : "For the Rotary admin team:";
        }

        return IsRotarianSubmission(submission)
            ? "For the Rotary representatives and support team:"
            : "For the Rotary representative:";
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
        string recipientSectionLabel,
        IReadOnlyList<string>? routingErrors = null)
    {
        var sections = new List<string>
        {
            Paragraph(greeting),
            SectionLabel(BuildSubmitterSectionLabel(submission))
        };

        sections.AddRange(introParagraphs.Select(Paragraph));
        sections.AddRange(
        [
            Paragraph("For reference, here is the information you submitted:"),
            SubmissionInformationBlock(submission)
        ]);

        if (routingErrors?.Count > 0)
        {
            sections.Add(Paragraph($"Routing notes: {Html(string.Join("; ", routingErrors))}"));
        }

        sections.Add(SectionLabel(recipientSectionLabel));
        var representativeIntro = IsRotarianSubmission(submission)
            ? "This question was submitted by a fellow Rotarian. "
            : string.Empty;
        sections.Add(Paragraph($"{representativeIntro}If you have any admin support questions, need advice about the process, need to add or remove email addresses for {Html(supportContext)}, or want a list of previous submissions, please contact {SupportEmailLink()}."));
        var closing = IsRotarianSubmission(submission)
            ? $"Thank you for participating in Rotary Youth Exchange and supporting the Study Abroad Scholarships through {SiteLink()}!"
            : $"Thank you for your interest in the Study Abroad Scholarships offered through Rotary Youth Exchange at {SiteLink()}!";
        sections.Add(Paragraph(closing));

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

    private static string SectionLabel(string text)
    {
        return $"<p><strong><u>{Html(text)}</u></strong></p>";
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
