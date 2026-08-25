using System.Net;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Services;

namespace RotaryEmailForwarding.FunctionApp.Email.DistrictTemplates;

// District 6600 owns the program details and links in this template. Keep district-specific
// content here so it can be reviewed and updated without changing shared email wording.
public sealed class District6600EmailTemplate : IDistrictEmailTemplate
{
    private const string LongTermApplicationUrl = "https://yehub.net/OER-obapp";
    private const string ShortTermApplicationUrl = "https://yehub.net/OER-stapp";
    private const string DistrictYouthExchangeUrl = "https://rotarydistrict6600.org/rye/";

    public string DistrictNumber => "6600";

    public bool Supports(InterestFormSubmitterType submitterType)
    {
        return submitterType is InterestFormSubmitterType.Student or InterestFormSubmitterType.Parent;
    }

    public DistrictEmailTemplateContent Render(
        NormalizedInterestFormSubmission submission,
        string defaultSubject)
    {
        var submitterType = SubmissionNormalizer.GetSubmitterType(submission.SubmissionType);
        var greeting = string.IsNullOrWhiteSpace(submission.Name)
            ? "Hi,"
            : $"Hi {Html(submission.Name.Trim())},";
        var applicationStep = submitterType == InterestFormSubmitterType.Parent
            ? "Talk with your student and complete the <strong>first page of an application</strong> to show your interest."
            : "Talk to your parents and complete the <strong>first page of an application</strong> to show your interest.";
        var submitterSectionLabel = submitterType == InterestFormSubmitterType.Parent
            ? "For the submitting family:"
            : "For the submitting student and family:";
        var departureYear = submission.ReceivedOnUtc.Year + 1;
        var returnYear = departureYear + 1;

        var sections = new List<string>
        {
            Paragraph(greeting),
            SectionLabel("For the District 6600 Rotary representative:"),
            Paragraph("For reference, here is the information submitted:"),
            EmailTemplateService.BuildSubmissionInformationBlock(submission),
            SectionLabel(submitterSectionLabel),
            Paragraph("Thank you for your interest in <strong>The Study Abroad Scholarship Program</strong> provided through <strong>Rotary Youth Exchange!</strong>"),
            Paragraph("This is much more than a study abroad program—it&rsquo;s a life-changing opportunity to gain independence, immerse yourself in a new culture, learn a language, build lifelong friendships, and stand out on college and scholarship applications."),
            Paragraph("<strong>Choose the experience that&rsquo;s right for you:</strong>"),
            Paragraph("<strong>Long-Term Exchange (9–11 Months) – The Study Abroad Scholarship</strong>"),
            "<ul>" +
            $"<li>Attend high school abroad from <strong>August {departureYear}–June/July {returnYear}</strong></li>" +
            "<li>Live with carefully selected host families</li>" +
            "<li><strong>Scholarship value: approximately $25,000</strong><br>Covers tuition and school fees, room and board, a monthly stipend, pre-departure orientation and training, 24-hour worldwide emergency assistance, and more.</li>" +
            "<li><strong>Family investment: approximately $6,000–$8,000</strong><br>Includes airfare, insurance, visa costs, spending money, application fee, etc.</li>" +
            "</ul>",
            Paragraph("<strong>Short-Term Exchange (4–12 Weeks)</strong>"),
            "<ul>" +
            "<li>Participate in a family-to-family exchange during the summer</li>" +
            "<li>Spend 4–6 weeks overseas, then host your exchange sibling in the United States</li>" +
            "<li><strong>Family investment: approximately $2,500–$3,500</strong><br>Includes airfare, insurance, spending money, application fee, etc.</li>" +
            "</ul>",
            Paragraph("Application fees are fully refunded if you are not accepted into the program."),
            Paragraph("<strong><u>What do I need to do now?</u></strong>"),
            Paragraph(applicationStep),
            Paragraph("This lets us know you are seriously considering the program. You can switch between the Long-Term (Study Abroad Scholarship) and Short-Term programs later, if needed."),
            Paragraph("Please select <strong>District 6600</strong> when applying."),
            Paragraph($"<strong>Long-Term Application:</strong><br>{Link(LongTermApplicationUrl, LongTermApplicationUrl)}"),
            Paragraph($"<strong>Short-Term Application:</strong><br>{Link(ShortTermApplicationUrl, ShortTermApplicationUrl)}"),
            Paragraph($"You can also learn more about both programs, browse frequently asked questions, and register for upcoming information sessions at {Link(DistrictYouthExchangeUrl, DistrictYouthExchangeUrl)}. You are welcome to attend an information session whether or not you have started an application."),
            Paragraph("If you have any questions, <strong>fill out the first page of the application</strong>, and we will then be happy to answer them."),
            Paragraph("<strong>Welcome to Rotary Youth Exchange!</strong>")
        };

        return new DistrictEmailTemplateContent(defaultSubject, string.Join(Environment.NewLine, sections));
    }

    private static string Paragraph(string content)
    {
        return $"<p>{content}</p>";
    }

    private static string SectionLabel(string content)
    {
        return Paragraph($"<strong><u>{Html(content)}</u></strong>");
    }

    private static string Link(string url, string label)
    {
        return $"<a href=\"{Html(url)}\">{Html(label)}</a>";
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
