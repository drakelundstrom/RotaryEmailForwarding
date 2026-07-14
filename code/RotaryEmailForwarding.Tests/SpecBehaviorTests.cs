using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
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
                    Country = "usa",
                    District = "District 6630",
                    EmailAddresses = ["rep@example.com"],
                    ZipCodes = ["44102"]
                }
            ],
            CancellationToken.None);

        var sender = new FakeEmailSender();
        var workflow = BuildWorkflow(repository, sender);
        var result = await workflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Student",
                Name = "Jordan Example",
                Age = "16",
                ParentEnteredAge = "15",
                StudentEmail = "jordan@example.com",
                StudentPhone = "555-0100",
                ParentEmail = "parent@example.com",
                ParentPhone = "555-0101",
                ContactEmail = "generic@example.com",
                ContactPhone = "555-0102",
                CountryOfResidence = "United States",
                State = "Ohio",
                City = "Cleveland",
                Zipcode = "44102-1234",
                OptionalSubmissionQuestion = "Can I choose a country?"
            },
            "corr-1",
            CancellationToken.None);

        Assert.Equal(EmailDeliveryStatus.Sent, result.Submission.EmailDeliveryStatus);
        Assert.NotNull(result.Submission.SentOnUtc);
        Assert.Equal(["District 6630"], result.Submission.RoutedDistricts);
        var message = Assert.Single(sender.SentMessages);
        Assert.Equal(
            ["rep@example.com", "jordan@example.com", "parent@example.com"],
            message.Recipients);
        Assert.True(message.IsBodyHtml);
        Assert.Contains("<p>Hello RYE District 6630 Representatives,</p>", message.Body);
        Assert.Contains(
            "has submitted a Rotary Youth Exchange contact form on the Study Abroad Scholarships website at <a href=\"https://studyabroadscholarships.org/\">studyabroadscholarships.org</a>.",
            message.Body);
        Assert.Contains("told to expect a follow up within 2 weeks", message.Body);
        Assert.Contains("operator@example.com", message.Body);
        Assert.Contains("<strong>Who are you?:</strong> Student", message.Body);
        Assert.Contains("<strong>Name:</strong> Jordan Example", message.Body);
        Assert.Contains("<strong>Current age (years):</strong> 16", message.Body);
        Assert.Contains("<strong>Current age of your student (years):</strong> 15", message.Body);
        Assert.Contains("<strong>Student's email:</strong> jordan@example.com", message.Body);
        Assert.Contains("<strong>Student's phone number:</strong> 555-0100", message.Body);
        Assert.Contains("<strong>Parent's email:</strong> parent@example.com", message.Body);
        Assert.Contains("<strong>Parent's phone number:</strong> 555-0101", message.Body);
        Assert.Contains("<strong>Contact email:</strong> generic@example.com", message.Body);
        Assert.Contains("<strong>Contact phone number:</strong> 555-0102", message.Body);
        Assert.Contains("<strong>Country of residence:</strong> USA", message.Body);
        Assert.Contains("<strong>State or province:</strong> Ohio", message.Body);
        Assert.Contains("<strong>City:</strong> Cleveland", message.Body);
        Assert.Contains("<strong>Zip code or first 3 of CDN postal code:</strong> 44102", message.Body);
        Assert.Contains("<strong>Question:</strong> Can I choose a country?", message.Body);
    }

    [Fact]
    public async Task Workflow_QuotaFailureLeavesSubmissionRetryPendingAndUnsent()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertCountryContactsAsync(
            [
                new ContactsForCountry
                {
                    Country = "mexico",
                    EmailAddresses = ["country@example.com"],
                    IsCertified = true
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
                ContactEmail = "jordan@example.com",
                CountryOfResidence = "Mexico"
            },
            "corr-2",
            CancellationToken.None);

        Assert.Equal(EmailDeliveryStatus.RetryPending, result.Submission.EmailDeliveryStatus);
        Assert.Null(result.Submission.SentOnUtc);
        Assert.NotNull(result.Submission.NextEmailAttemptOnUtc);
    }

    [Fact]
    public async Task Routing_UncertifiedMexicoUsesManualReviewPath()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertCountryContactsAsync(
            [
                new ContactsForCountry
                {
                    Country = "mexico",
                    IsCertified = false
                }
            ],
            CancellationToken.None);

        var route = await new SubmissionRoutingService(repository, new FakeClock(Now)).RouteAsync(
            SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest
                {
                    CountryOfResidence = "Mexico",
                    ContactEmail = "student@example.com"
                },
                Now),
            CancellationToken.None);

        Assert.Equal(SubmissionRouteKind.UncertifiedCountry, route.Kind);
    }

    [Fact]
    public async Task Workflow_CopiesSupportOnlyForRotarianAndOtherSubmissions()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "6630",
                    EmailAddresses = ["rep@example.com"],
                    ZipCodes = ["44102"]
                }
            ],
            CancellationToken.None);

        var studentSender = new FakeEmailSender();
        var studentWorkflow = BuildWorkflow(repository, studentSender, supportEmail: "support@example.com");
        await studentWorkflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Student",
                Name = "Student Example",
                StudentEmail = "student@example.com",
                CountryOfResidence = "United States",
                Zipcode = "44102"
            },
            "corr-support-student",
            CancellationToken.None);

        var studentMessage = Assert.Single(studentSender.SentMessages);
        Assert.DoesNotContain("support@example.com", studentMessage.Recipients);
        Assert.True(studentMessage.IsBodyHtml);
        Assert.Contains("<strong>Student's email:</strong> student@example.com", studentMessage.Body);
        Assert.DoesNotContain("Parent's email", studentMessage.Body);
        Assert.DoesNotContain("Contact email", studentMessage.Body);
        Assert.DoesNotContain("Current age", studentMessage.Body);

        var rotarianSender = new FakeEmailSender();
        var rotarianWorkflow = BuildWorkflow(repository, rotarianSender, supportEmail: "support@example.com");
        await rotarianWorkflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Rotarian",
                Name = "Rotarian Example",
                ContactEmail = "rotarian@example.com",
                CountryOfResidence = "United States",
                Zipcode = "44102"
            },
            "corr-support-rotarian",
            CancellationToken.None);

        var rotarianMessage = Assert.Single(rotarianSender.SentMessages);
        Assert.Contains("support@example.com", rotarianMessage.Recipients);

        var otherSender = new FakeEmailSender();
        var otherWorkflow = BuildWorkflow(repository, otherSender, supportEmail: "support@example.com");
        await otherWorkflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Other",
                Name = "Other Example",
                ContactEmail = "other@example.com",
                CountryOfResidence = "United States",
                Zipcode = "44102"
            },
            "corr-support-other",
            CancellationToken.None);

        var otherMessage = Assert.Single(otherSender.SentMessages);
        Assert.Contains("support@example.com", otherMessage.Recipients);
    }

    [Fact]
    public async Task Workflow_IncludesStudentAndParentRecipientsWhenBothEmailsAreProvided()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "6630",
                    EmailAddresses = ["rep@example.com"],
                    ZipCodes = ["44102"]
                }
            ],
            CancellationToken.None);

        var sender = new FakeEmailSender();
        var workflow = BuildWorkflow(repository, sender);
        await workflow.ProcessAsync(
            new InterestFormSubmissionRequest
            {
                SubmissionType = "Parent",
                Name = "Parent Example",
                StudentEmail = "student@example.com",
                ParentEmail = "parent@example.com",
                ContactEmail = "contact@example.com",
                CountryOfResidence = "United States",
                Zipcode = "44102"
            },
            "corr-student-parent",
            CancellationToken.None);

        var message = Assert.Single(sender.SentMessages);
        Assert.Equal(
            ["rep@example.com", "student@example.com", "parent@example.com", "contact@example.com"],
            message.Recipients);
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
    public async Task Delivery_RecordsPartialAttemptsWhenSenderThrows()
    {
        var sender = new FakeEmailSender
        {
            ThrowOnMessageKey = "two",
            ExceptionToThrow = new FormatException("Invalid email address.")
        };
        var orchestrator = new EmailDeliveryOrchestrator(sender, new FakeClock(Now));
        var submission = SubmissionNormalizer.Normalize(new InterestFormSubmissionRequest(), Now);

        var delivered = await orchestrator.DeliverAsync(
            submission,
            [
                new OutboundEmailMessage("one", OutboundEmailMessageType.OperatorFallback, ["operator@example.com"], "subject", "body"),
                new OutboundEmailMessage("two", OutboundEmailMessageType.SubmitterConfirmation, ["bad-address"], "subject", "body")
            ],
            CancellationToken.None);

        Assert.Equal(EmailDeliveryStatus.TerminalFailed, delivered.EmailDeliveryStatus);
        Assert.Null(delivered.SentOnUtc);
        Assert.Null(delivered.NextEmailAttemptOnUtc);
        Assert.Equal(2, delivered.EmailDeliveryAttempts.Count);
        Assert.Equal(OutboundEmailAttemptStatus.Succeeded, delivered.EmailDeliveryAttempts[0].Status);
        Assert.Equal(OutboundEmailAttemptStatus.TerminalFailed, delivered.EmailDeliveryAttempts[1].Status);
        Assert.Equal("FormatException", delivered.EmailDeliveryAttempts[1].ProviderCode);
    }

    [Fact]
    public async Task Reporting_IncludesSameMonthFinalMonthAndCountryBreakdown()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "usa" },
                new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "France" },
                new DateTimeOffset(2026, 2, 6, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);

        var buckets = await new ReportingService(repository).GenerateSubmissionsByMonthAsync(
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("February 2026", bucket.Month);
        Assert.Collection(
            bucket.CountryResults,
            result =>
            {
                Assert.Equal("USA", result.Name);
                Assert.Equal(1, result.NumberOfSubmissions);
            },
            result =>
            {
                Assert.Equal("CANADA", result.Name);
                Assert.Equal(0, result.NumberOfSubmissions);
            },
            result =>
            {
                Assert.Equal("MEXICO", result.Name);
                Assert.Equal(0, result.NumberOfSubmissions);
            },
            result =>
            {
                Assert.Equal("other country", result.Name);
                Assert.Equal(1, result.NumberOfSubmissions);
            });
    }

    [Fact]
    public async Task Reporting_DistrictSubmissionsAreFoundByDistrictCountryAndZipcode()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 1",
                    EmailAddresses = ["usa@example.com"],
                    ZipCodes = ["12345"]
                },
                new ContactsForDistrict
                {
                    Country = "canada",
                    District = "District 2",
                    EmailAddresses = ["canada@example.com"],
                    ZipCodes = ["123"]
                }
            ],
            CancellationToken.None);

        var usaSubmission = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "12345-0000" },
            Now);
        var canadaSubmission = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest { CountryOfResidence = "Canada", Zipcode = "123 ABC" },
            Now);
        await repository.InsertSubmissionAsync(usaSubmission, CancellationToken.None);
        await repository.InsertSubmissionAsync(canadaSubmission, CancellationToken.None);

        var submissions = await new ReportingService(repository).GetSubmissionsByDistrictAsync(
            "District 1",
            Now.AddYears(-1),
            CancellationToken.None);

        var submission = Assert.Single(submissions);
        Assert.Equal(usaSubmission.Id, submission.Id);
    }

    [Fact]
    public async Task Reporting_InterestFormsByDistrictQuarterReturnsMarkdownTable()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 1",
                    EmailAddresses = ["one@example.com"],
                    ZipCodes = ["12345"]
                },
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 2",
                    EmailAddresses = ["two@example.com"],
                    ZipCodes = ["12345"]
                },
                new ContactsForDistrict
                {
                    Country = "canada",
                    District = "District 3",
                    EmailAddresses = ["three@example.com"],
                    ZipCodes = ["A1A"]
                }
            ],
            CancellationToken.None);

        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "12345-0000" },
                new DateTimeOffset(2024, 7, 15, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "99999" },
                new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "Canada", Zipcode = "A1A 1A1" },
                new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "Mexico" },
                new DateTimeOffset(2026, 2, 5, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "France" },
                new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "12345" },
                new DateTimeOffset(2024, 6, 30, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);

        var markdown = await new ReportingService(repository).GenerateInterestFormsByDistrictQuarterMarkdownAsync(
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.StartsWith(
            "| Country | District | 2024 Q3 | 2024 Q4 | 2025 Q1 | 2025 Q2 | 2025 Q3 | 2025 Q4 | 2026 Q1 | 2026 Q2 | 2026 Q3 |",
            markdown);
        Assert.DoesNotContain("2024 Q2", markdown);
        Assert.Contains("| USA | District 1 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |", markdown);
        Assert.Contains("| USA | District 2 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |", markdown);
        Assert.Contains("| USA | Other | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |", markdown);
        Assert.Contains("| Canada | District 3 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |", markdown);
        Assert.Contains("| Mexico | Other | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 |", markdown);
        Assert.Contains("| Other countries | Other | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |", markdown);
    }

    [Fact]
    public async Task Reporting_InterestFormsByDistrictQuarterIgnoresTestingDistrict321()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 1",
                    EmailAddresses = ["one@example.com"],
                    ZipCodes = ["12345"]
                },
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 321",
                    EmailAddresses = ["test@example.com"],
                    ZipCodes = ["32100"]
                }
            ],
            CancellationToken.None);

        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "12345" },
                new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.InsertSubmissionAsync(
            WithCosmosTimestamp(SubmissionNormalizer.Normalize(
                new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "32100" },
                new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        var routedTestingDistrictSubmission = WithCosmosTimestamp(SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest { CountryOfResidence = "United States", Zipcode = "99999" },
            new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero))) with
        {
            RoutedDistricts = ["321"]
        };
        await repository.InsertSubmissionAsync(routedTestingDistrictSubmission, CancellationToken.None);

        var markdown = await new ReportingService(repository).GenerateInterestFormsByDistrictQuarterMarkdownAsync(
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Contains("| USA | District 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |", markdown);
        Assert.DoesNotContain("| USA | District 321 |", markdown);
        Assert.DoesNotContain("| USA | 321 |", markdown);
        Assert.DoesNotContain("| USA | Other |", markdown);
    }

    [Fact]
    public async Task Reporting_InterestFormsByDistrictQuarterCountsOldAndNewSubmissionShapes()
    {
        var repository = new InMemoryApplicationRepository();
        await repository.UpsertDistrictContactsAsync(
            [
                new ContactsForDistrict
                {
                    Country = "usa",
                    District = "District 6630",
                    EmailAddresses = ["usa@example.com"],
                    ZipCodes = ["44102"]
                },
                new ContactsForDistrict
                {
                    Country = "canada",
                    District = "District 5550",
                    EmailAddresses = ["canada@example.com"],
                    ZipCodes = ["A1A"]
                }
            ],
            CancellationToken.None);

        var legacyJsonTimestamp = new DateTimeOffset(2024, 8, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var oldJsonSubmission = JsonConvert.DeserializeObject<NormalizedInterestFormSubmission>(
            $$"""
            {
              "id": "legacy-json",
              "Type": "InterestFormSubmission",
              "_ts": {{legacyJsonTimestamp}},
              "IsInterestedInHosting": "no",
              "SubmissionQuestion": "Can I study in Japan?",
              "Gender": "female",
              "Email": "legacy@example.com",
              "Phone": "555-111-2222",
              "CountryOfResidence": "The United States",
              "Zipcode": "44102-1234",
              "CountryChoiceOne": "Japan",
              "ReceivedOnUtc": "2026-07-01T00:00:00Z"
            }
            """)!;
        var oldCosmosTimestampSubmission = new NormalizedInterestFormSubmission
        {
            Id = "legacy-ts",
            CountryOfResidence = "Canada",
            Zipcode = "A1A 1A1",
            ReceivedOnUtc = default,
            CosmosTimestamp = new DateTimeOffset(2025, 10, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        };
        var newSubmission = SubmissionNormalizer.Normalize(
            new InterestFormSubmissionRequest
            {
                CountryOfResidence = "Mexico",
                SubmissionType = "Student",
                StudentEmail = "student@example.com"
            },
            new DateTimeOffset(2024, 7, 2, 0, 0, 0, TimeSpan.Zero)) with
        {
            CosmosTimestamp = new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()
        };

        await repository.InsertSubmissionAsync(oldJsonSubmission, CancellationToken.None);
        await repository.InsertSubmissionAsync(oldCosmosTimestampSubmission, CancellationToken.None);
        await repository.InsertSubmissionAsync(newSubmission, CancellationToken.None);

        var markdown = await new ReportingService(repository).GenerateInterestFormsByDistrictQuarterMarkdownAsync(
            new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Contains("| USA | District 6630 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |", markdown);
        Assert.Contains("| Canada | District 5550 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 0 | 0 |", markdown);
        Assert.Contains("| Mexico | Other | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |", markdown);
    }

    [Fact]
    public void Configuration_DoesNotDefaultMaintenanceEmails()
    {
        var configuration = new ConfigurationBuilder().Build();

        var appConfiguration = AppConfiguration.FromConfiguration(configuration);

        Assert.Equal(string.Empty, appConfiguration.SendingEmailAddress);
        Assert.Equal(string.Empty, appConfiguration.OperatorEmail);
        Assert.Equal(string.Empty, appConfiguration.SupportEmail);
    }

    [Fact]
    public void Configuration_ReadsSenderMaintenanceAndSafeRecipientFromSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["sendingEmailAddress"] = "sender@example.test",
                ["operatorEmail"] = "operator@example.test",
                ["supportEmail"] = "support@example.test",
                ["nonProductionSafeRecipient"] = "safe@example.test"
            })
            .Build();

        var appConfiguration = AppConfiguration.FromConfiguration(configuration);

        Assert.Equal("sender@example.test", appConfiguration.SendingEmailAddress);
        Assert.Equal("operator@example.test", appConfiguration.OperatorEmail);
        Assert.Equal("support@example.test", appConfiguration.SupportEmail);
        Assert.Equal("safe@example.test", appConfiguration.NonProductionSafeRecipient);
    }

    [Fact]
    public async Task SmtpSender_BlocksNonProductionEmailWithoutSafeRecipient()
    {
        var sender = new SmtpEmailSender(new AppConfiguration
        {
            AppEnvironment = "test",
            SendingEmailAddress = "sender@example.test",
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

    private static SubmissionWorkflow BuildWorkflow(
        InMemoryApplicationRepository repository,
        FakeEmailSender sender,
        string supportEmail = "")
    {
        var clock = new FakeClock(Now);
        var appConfiguration = new AppConfiguration
        {
            AppEnvironment = "test",
            SendingEmailAddress = "operator@example.com",
            OperatorEmail = "operator@example.com",
            SupportEmail = supportEmail,
            NonProductionSafeRecipient = "sink@example.com"
        };

        return new SubmissionWorkflow(
            repository,
            new SubmissionRoutingService(repository, clock),
            new EmailTemplateService(appConfiguration),
            new EmailDeliveryOrchestrator(sender, clock),
            clock);
    }

    private static NormalizedInterestFormSubmission WithCosmosTimestamp(NormalizedInterestFormSubmission submission)
    {
        return submission with
        {
            CosmosTimestamp = submission.ReceivedOnUtc.ToUnixTimeSeconds()
        };
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<OutboundEmailMessage> SentMessages { get; } = [];

        public EmailSendResult NextResult { get; init; } = EmailSendResult.Success();

        public string? ThrowOnMessageKey { get; init; }

        public Exception ExceptionToThrow { get; init; } = new InvalidOperationException("Send failed.");

        public Task<EmailSendResult> SendAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            if (string.Equals(message.MessageKey, ThrowOnMessageKey, StringComparison.Ordinal))
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(NextResult);
        }
    }
}
