using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email.DistrictTemplates;

public sealed class DistrictEmailTemplateRegistry(IEnumerable<IDistrictEmailTemplate> templates)
{
    private readonly IReadOnlyList<IDistrictEmailTemplate> templates = templates.ToList();

    public static DistrictEmailTemplateRegistry CreateDefault()
    {
        return new DistrictEmailTemplateRegistry([new District6600EmailTemplate()]);
    }

    public IDistrictEmailTemplate? FindTemplate(
        NormalizedInterestFormSubmission submission,
        SubmissionRoute route)
    {
        if (route.Kind != SubmissionRouteKind.District || route.DistrictContacts.Count != 1)
        {
            return null;
        }

        var routedDistrictNumber = NormalizeDistrictNumber(route.DistrictContacts[0].District);
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);

        return templates.FirstOrDefault(template =>
            string.Equals(
                NormalizeDistrictNumber(template.DistrictNumber),
                routedDistrictNumber,
                StringComparison.OrdinalIgnoreCase)
            && template.Supports(submitterType));
    }

    private static string NormalizeDistrictNumber(string? district)
    {
        var normalized = district?.Trim() ?? string.Empty;
        return normalized.StartsWith("district ", StringComparison.OrdinalIgnoreCase)
            ? normalized["district ".Length..].Trim()
            : normalized;
    }
}
