using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email.DistrictTemplates;

public sealed record DistrictEmailTemplateContent(string Subject, string Body);

public interface IDistrictEmailTemplate
{
    string DistrictNumber { get; }

    bool Supports(InterestFormSubmitterType submitterType);

    DistrictEmailTemplateContent Render(
        NormalizedInterestFormSubmission submission,
        string defaultSubject);
}
