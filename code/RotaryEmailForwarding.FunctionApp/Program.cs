using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RotaryEmailForwarding.FunctionApp.Authorization;
using RotaryEmailForwarding.FunctionApp.Configuration;
using RotaryEmailForwarding.FunctionApp.Email;
using RotaryEmailForwarding.FunctionApp.Reporting;
using RotaryEmailForwarding.FunctionApp.Retry;
using RotaryEmailForwarding.FunctionApp.Routing;
using RotaryEmailForwarding.FunctionApp.Services;
using RotaryEmailForwarding.FunctionApp.Storage;
using RotaryEmailForwarding.FunctionApp.Workflow;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var appConfiguration = AppConfiguration.FromConfiguration(context.Configuration);
        services.AddSingleton(appConfiguration);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IApplicationRepository>(_ =>
            string.IsNullOrWhiteSpace(appConfiguration.DatabaseConnectionString)
                ? new InMemoryApplicationRepository()
                : new CosmosApplicationRepository(appConfiguration));
        services.AddSingleton<SubmissionRoutingService>();
        services.AddSingleton<EmailTemplateService>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<EmailDeliveryOrchestrator>();
        services.AddSingleton<SubmissionWorkflow>();
        services.AddSingleton<EmailRetryService>();
        services.AddSingleton<ReportingService>();
        services.AddSingleton<AdminAuthorizationService>();
    })
    .Build();

await host.RunAsync();
