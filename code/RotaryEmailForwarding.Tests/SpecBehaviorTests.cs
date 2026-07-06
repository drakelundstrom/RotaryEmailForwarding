using Microsoft.Extensions.Configuration;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Domain;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Models;
using RotaryEmailForwarding.FunctionApp.Reporting;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Retry;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;
using RotaryEmailForwarding.FunctionApp.Workflow;
using Xunit;

namespace RotaryEmailForwarding.Tests;

public sealed class SpecBehaviorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Workflow_RoutesDistrictSubmissionAndSetsSentOnlyAfterAllEmailSucceeds()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    DistrictName = "District 6630",
                    EmailAddresses = ["rep@example.com"],
                    Zipcodes = ["44102"],
                    EffectiveFromUtc = Now.AddDays(-1)
                }
            ],
            CancellationToken.None);

        var sender = new FakeEmailSender();
        var workflow = BuildWorkflow(repository, sender);
        var result = await workflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                Name = "Jordan Example",
                Email = "jordan@example.com",
                CountryOfResidence = "United States",
                Zipcode = "44102-1234"
            },
            """{"name":"Jordan Example"}""",
            "corr-1",
            CancellationToken.None);

        Assert.Equal(EmailDeliveryStatus.Sent, result.Submission.EmailDeliveryStatus);
        Assert.NotNull(result.Submission.SentOnUtc);
        Assert.Equal(["District 6630"], result.Submission.RoutedDistricts);
        Assert.Equal(2, sender.SentMessages.Count);
    }

    [Fact]
    public async Task Workflow_QuotaFailureLeavesSubmissionRetryPendingAndUnsent()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertCountryContactsAsync(
            [
                new ContactsForCountry
                {
                    CountryName = "france",
                    EmailAddresses = ["country@example.com"],
                    IsCertified = true,
                    EffectiveFromUtc = Now.AddDays(-1)
                }
            ],
            CancellationToken.None);

        var sender = new FakeEmailSender
        {
            NextResult = EmailSendResult.Failed(OutboundEmailAttemptStatus.QuotaExceeded, "QuotaExceeded", "max emails per day")
        };
        var workflow = BuildWorkflow(repository, sender);
        var result = await workflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                Name = "Jordan Example",
                Email = "jordan@example.com",
                CountryOfResidence = "France"
            },
            "{}",
            "corr-2",
            CancellationToken.None);

        Assert.Equal(EmailDeliveryStatus.RetryPending, result.Submission.EmailDeliveryStatus);
        Assert.Null(result.Submission.SentOnUtc);
        Assert.NotNull(result.Submission.NextEmailAttemptOnUtc);
    }

    [Fact]
    public async Task Routing_UncertifiedCountryUsesRejectionPath()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertCountryContactsAsync(
            [
                new ContactsForCountry
                {
                    CountryName = "germany",
                    IsCertified = false,
                    EffectiveFromUtc = Now.AddDays(-1)
                }
            ],
            CancellationToken.None);

        var route = await new SubmissionRoutingService(repository, new FakeClock(Now)).RouteAsync(
            SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest
                {
                    CountryOfResidence = "Germany",
                    Email = "student@example.com"
                },
                Now),
            CancellationToken.None);

        Assert.Equal(SubmissionRouteKind.UncertifiedCountry, route.Kind);
    }

    [Fact]
    public async Task Delivery_RetryDoesNotResendAlreadySuccessfulMessages()
    {
        var sender = new FakeEmailSender();
        var orchestrator = new EmailDeliveryOrchestrator(sender, new FakeClock(Now));
        var submission = SubmissionNormalizer.Normalize(new InterestFormSubmissionRequest(), Now) with
        {
            EmailDeliveryAttempts =
            [
                new OutboundEmailAttempt
                {
                    MessageKey = "one",
                    MessageType = OutboundEmailMessageType.OperatorFallback,
                    Recipients = ["operator@example.com"],
                    AttemptedOnUtc = Now,
                    Status = OutboundEmailAttemptStatus.Succeeded
                }
            ]
        };

        var delivered = await orchestrator.DeliverAsync(
            submission,
            [
                new OutboundEmailMessage("one", OutboundEmailMessageType.OperatorFallback, ["operator@example.com"], "subject", "body"),
                new OutboundEmailMessage("two", OutboundEmailMessageType.SubmitterConfirmation, ["student@example.com"], "subject", "body")
            ],
            CancellationToken.None);

        var sentMessage = Assert.Single(sender.SentMessages);
        Assert.Equal("two", sentMessage.MessageKey);
        Assert.Equal(EmailDeliveryStatus.Sent, delivered.EmailDeliveryStatus);
    }

    [Fact]
    public async Task Reporting_IncludesSameMonthAndFinalMonth()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.InsertSubmissionAsync(
            SubmissionNormalizer.Normalize(new InterestFormSubmissionRequest(), new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);

        var buckets = await new ReportingService(repository).GenerateSubmissionsByMonthAsync(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal(2026, bucket.Year);
        Assert.Equal(2, bucket.Month);
        Assert.Equal(1, bucket.Count);
    }

    [Fact]
    public void Configuration_DefaultsSenderEmailToRequestedOperatorAddress()
    {
        var configuration = new ConfigurationBuilder().Build();

        var appConfiguration = AppConfiguration.FromConfiguration(configuration);

        Assert.Equal("DrakeLundstrom95@gmail.com", appConfiguration.SendingEmailAddress);
        Assert.Equal("DrakeLundstrom95@gmail.com", appConfiguration.OperatorEmail);
    }

    [Fact]
    public void Configuration_ReadsSenderAndSafeRecipientFromSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["sendingEmailAddress"] = "DrakeLundstrom95@gmail.com",
                ["nonProductionSafeRecipient"] = "DrakeLundstrom95@gmail.com"
            })
            .Build();

        var appConfiguration = AppConfiguration.FromConfiguration(configuration);

        Assert.Equal("DrakeLundstrom95@gmail.com", appConfiguration.SendingEmailAddress);
        Assert.Equal("DrakeLundstrom95@gmail.com", appConfiguration.NonProductionSafeRecipient);
    }

    [Fact]
    public async Task SmtpSender_BlocksNonProductionEmailWithoutSafeRecipient()
    {
        var sender = new SmtpEmailSender(new AppConfiguration
        {
            AppEnvironment = "test",
            SendingEmailAddress = "DrakeLundstrom95@gmail.com",
            SendingEmailPassword = "unused-because-send-is-blocked"
        });

        var result = await sender.SendAsync(
            new OutboundEmailMessage("message", OutboundEmailMessageType.OperatorFallback, ["real@example.com"], "subject", "body"),
            CancellationToken.None);

        Assert.Equal(OutboundEmailAttemptStatus.TerminalFailed, result.Status);
        Assert.Equal("UnsafeNonProductionEmailBlocked", result.ProviderCode);
    }

    [Fact]
    public void SmtpSender_ClassifiesQuotaFailuresAsQuotaExceeded()
    {
        var result = SmtpEmailSender.Classify(new InvalidOperationException("Provider rejected send: max emails per day exceeded."));

        Assert.Equal(OutboundEmailAttemptStatus.QuotaExceeded, result.Status);
        Assert.Equal("QuotaExceeded", result.ProviderCode);
    }

    [Fact]
    public void RetryTimeZone_ResolvesEasternAcrossSupportedRuntimeIds()
    {
        var timeZone = RetryTimeZone.Resolve("Eastern Standard Time");

        Assert.NotEqual(TimeZoneInfo.Utc.Id, timeZone.Id);
    }

    private static SubmissionWorkflow BuildWorkflow(InMemoryApplicationRepository repository, FakeEmailSender sender)
    {
        var clock = new FakeClock(Now);
        var appConfiguration = new AppConfiguration
        {
            AppEnvironment = "test",
            SendingEmailAddress = "operator@example.com",
            NonProductionSafeRecipient = "sink@example.com"
        };

        return new SubmissionWorkflow(
            repository,
            new SubmissionRoutingService(repository, clock),
            new EmailTemplateService(appConfiguration),
            new EmailDeliveryOrchestrator(sender, clock),
            clock);
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<OutboundEmailMessage> SentMessages { get; } = [];

        public EmailSendResult NextResult { get; init; } = EmailSendResult.Success();

        public Task<EmailSendResult> SendAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.FromResult(NextResult);
        }
    }
}
