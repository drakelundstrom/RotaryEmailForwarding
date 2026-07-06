using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;

namespace RotaryEmailForwarding.FunctionApp.Email;

public sealed class EmailTemplateService(AppConfiguration configuration)
{
    public IReadOnlyList<OutboundEmailMessage> BuildMessages(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route,
        string rawSubmissionJson)
    {
        var messages = new List<OutboundEmailMessage>();

        switch (route.Kind)
        {
            case SubmissionRouteKind.District:
                messages.Add(BuildDistrictRepresentativeMessage(submission, route));
                AddSubmitterMessage(messages, BuildConfirmationMessage(submission, route));
                break;

            case SubmissionRouteKind.Country:
                messages.Add(BuildCountryRepresentativeMessage(submission, route));
                AddSubmitterMessage(messages, BuildConfirmationMessage(submission, route));
                break;

            case SubmissionRouteKind.UncertifiedCountry:
                AddSubmitterMessage(messages, BuildRejectionMessage(submission, route));
                break;

            case SubmissionRouteKind.Fallback:
                messages.Add(BuildOperatorFallbackMessage(submission, route, rawSubmissionJson));
                AddSubmitterMessage(messages, BuildConfirmationMessage(submission, route));
                break;
        }

        return messages;
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

    private OutboundEmailMessage BuildDistrictRepresentativeMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = route.DistrictContacts
            .SelectMany(contact => contact.EmailAddresses)
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var districtNames = string.Join(", ", route.DistrictContacts.Select(contact => contact.DistrictName));
        var uncertainty = route.HasMultipleDistrictMatches
            ? $"{Environment.NewLine}The system is not sure which district applies, so this was sent to all matching districts: {districtNames}.{Environment.NewLine}"
            : string.Empty;

        return new OutboundEmailMessage(
            $"district:{submission.Id}",
            OutboundEmailMessageType.DistrictRepresentative,
            recipients,
            $"Interest form submission from {UnknownIfBlank(submission.Name)}",
            $"{uncertainty}{StudentInformationBlock(submission)}");
    }

    private OutboundEmailMessage BuildCountryRepresentativeMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        var recipients = route.CountryContact?.EmailAddresses
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return new OutboundEmailMessage(
            $"country:{submission.Id}",
            OutboundEmailMessageType.CountryRepresentative,
            recipients,
            $"Interest form submission from {UnknownIfBlank(submission.Name)}",
            StudentInformationBlock(submission));
    }

    private OutboundEmailMessage? BuildConfirmationMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        if (!EmailAddressUtility.IsUsable(submission.Email))
        {
            return null;
        }

        var routeSummary = route.Kind switch
        {
            SubmissionRouteKind.District when route.DistrictContacts.Count > 0 =>
                $"A representative from district {string.Join(", ", route.DistrictContacts.Select(contact => contact.DistrictName))} will follow up.",
            SubmissionRouteKind.Country when route.CountryContact is not null =>
                $"A representative for {route.CountryContact.CountryName} will follow up.",
            _ => "A representative will follow up."
        };

        return new OutboundEmailMessage(
            $"submitter-confirmation:{submission.Id}",
            OutboundEmailMessageType.SubmitterConfirmation,
            [submission.Email!.Trim()],
            "Thank you for your interest in Rotary Youth Exchange",
            $"Thank you for contacting Study Abroad Scholarships.{Environment.NewLine}{routeSummary}{Environment.NewLine}{Environment.NewLine}{StudentInformationBlock(submission)}");
    }

    private OutboundEmailMessage? BuildRejectionMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        if (!EmailAddressUtility.IsUsable(submission.Email))
        {
            return null;
        }

        var countryName = route.CountryContact?.CountryName ?? UnknownIfBlank(submission.CountryOfResidence);

        return new OutboundEmailMessage(
            $"submitter-rejection:{submission.Id}",
            OutboundEmailMessageType.SubmitterRejection,
            [submission.Email!.Trim()],
            "Rotary Youth Exchange availability",
            $"Thank you for your interest in Rotary Youth Exchange. At this time, {countryName} is not certified for this program through this site. Please contact {configuration.OperatorEmail} with questions.{Environment.NewLine}{Environment.NewLine}{StudentInformationBlock(submission)}");
    }

    private OutboundEmailMessage BuildOperatorFallbackMessage(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route,
        string rawSubmissionJson)
    {
        return new OutboundEmailMessage(
            $"operator-fallback:{submission.Id}",
            OutboundEmailMessageType.OperatorFallback,
            [configuration.OperatorEmail],
            "Interest form submission needs manual routing",
            $"Routing errors: {string.Join("; ", route.Errors)}{Environment.NewLine}{Environment.NewLine}{StudentInformationBlock(submission)}{Environment.NewLine}{Environment.NewLine}Raw submission JSON:{Environment.NewLine}{rawSubmissionJson}");
    }

    private static void AddSubmitterMessage(List<OutboundEmailMessage> messages, OutboundEmailMessage? message)
    {
        if (message is not null)
        {
            messages.Add(message);
        }
    }

    private static string StudentInformationBlock(NormalizedInterestFormSubmission submission)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Student information:",
            $"Name: {UnknownIfBlank(submission.Name)}",
            $"Age: {UnknownIfBlank(submission.Age)}",
            $"Gender: {UnknownIfBlank(submission.Gender)}",
            $"Email: {UnknownIfBlank(submission.Email)}",
            $"Phone: {UnknownIfBlank(submission.Phone)}",
            $"Country of residence: {UnknownIfBlank(submission.CountryOfResidence)}",
            $"State: {UnknownIfBlank(submission.State)}",
            $"City: {UnknownIfBlank(submission.City)}",
            $"Zipcode/postal code: {UnknownIfBlank(submission.Zipcode)}",
            $"Country choice one: {UnknownIfBlank(submission.CountryChoiceOne)}",
            $"Country choice two: {UnknownIfBlank(submission.CountryChoiceTwo)}",
            $"Country choice three: {UnknownIfBlank(submission.CountryChoiceThree)}",
            $"Country choice four: {UnknownIfBlank(submission.CountryChoiceFour)}",
            $"Question: {UnknownIfBlank(submission.SubmissionQuestion)}"
        });
    }

    private static string UnknownIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }
}
