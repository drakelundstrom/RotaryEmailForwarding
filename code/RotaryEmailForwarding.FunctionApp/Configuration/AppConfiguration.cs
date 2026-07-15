using Microsoft.Extensions.Configuration;

namespace RotaryEmailForwarding.FunctionApp.Configuration;

public sealed record AppConfiguration
{
    public string AppEnvironment { get; init; } = "local";

    public string CosmosDatabaseName { get; init; } = "EmailForwarding";

    public string CosmosContainerName { get; init; } = "ContactInfoAndRequestsByType";

    public string? DatabaseConnectionString { get; init; }

    public string SendingEmailAddress { get; init; } = string.Empty;

    public string OperatorEmail { get; init; } = string.Empty;

    public string SupportEmail { get; init; } = string.Empty;

    public string? SendingEmailPassword { get; init; }

    public string MailHost { get; init; } = "smtp.gmail.com";

    public int MailPort { get; init; } = 587;

    public string MailSecurityMode { get; init; } = "StartTls";

    public string EmailRetryTimeZone { get; init; } = "Eastern Standard Time";

    public string? AdminApiKey { get; init; }

    public string? NonProductionSafeRecipient { get; init; }

    public bool AllowUnsafeNonProductionEmail { get; init; }

    public int MaxRequestBodyBytes { get; init; } = 128 * 1024;

    public bool IsProduction => string.Equals(AppEnvironment, "prod", StringComparison.OrdinalIgnoreCase);

    public static AppConfiguration FromConfiguration(IConfiguration configuration)
    {
        var sendingEmailAddress = configuration["sendingEmailAddress"] ?? string.Empty;
        var operatorEmail = configuration["operatorEmail"] ?? sendingEmailAddress;

        return new AppConfiguration
        {
            AppEnvironment = configuration["appEnvironment"] ?? "local",
            CosmosDatabaseName = configuration["cosmosDatabaseName"] ?? "EmailForwarding",
            CosmosContainerName = configuration["cosmosContainerName"] ?? "ContactInfoAndRequestsByType",
            DatabaseConnectionString = configuration["databaseConnectionString"],
            SendingEmailAddress = sendingEmailAddress,
            OperatorEmail = operatorEmail,
            SupportEmail = configuration["supportEmail"] ?? operatorEmail,
            SendingEmailPassword = configuration["sendingEmailPassword"],
            MailHost = configuration["mailHost"] ?? "smtp.gmail.com",
            MailPort = int.TryParse(configuration["mailPort"], out var port) ? port : 587,
            MailSecurityMode = configuration["mailSecurityMode"] ?? "StartTls",
            EmailRetryTimeZone = configuration["emailRetryTimeZone"] ?? "Eastern Standard Time",
            AdminApiKey = configuration["adminApiKey"],
            NonProductionSafeRecipient = configuration["nonProductionSafeRecipient"],
            AllowUnsafeNonProductionEmail = bool.TryParse(configuration["allowUnsafeNonProductionEmail"], out var allowUnsafe)
                && allowUnsafe,
            MaxRequestBodyBytes = int.TryParse(configuration["maxRequestBodyBytes"], out var maxBytes)
                ? maxBytes
                : 128 * 1024
        };
    }
}
