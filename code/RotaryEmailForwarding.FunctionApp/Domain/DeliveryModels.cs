namespace RotaryEmailForwarding.FunctionApp.Domain;

public enum EmailDeliveryStatus
{
    Pending,
    Sent,
    RetryPending,
    TerminalFailed
}

public enum OutboundEmailMessageType
{
    DistrictRepresentative,
    CountryRepresentative,
    SubmitterConfirmation,
    SubmitterRejection,
    OperatorFallback,
    OperatorFailure
}

public enum OutboundEmailAttemptStatus
{
    Succeeded,
    RetryableFailed,
    QuotaExceeded,
    TerminalFailed,
    Skipped
}

public sealed record OutboundEmailMessage(
    string MessageKey,
    OutboundEmailMessageType MessageType,
    IReadOnlyList<string> Recipients,
    string Subject,
    string Body);

public sealed record OutboundEmailAttempt
{
    public required string MessageKey { get; init; }

    public required OutboundEmailMessageType MessageType { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public required DateTimeOffset AttemptedOnUtc { get; init; }

    public required OutboundEmailAttemptStatus Status { get; init; }

    public string? ProviderCode { get; init; }

    public string? ProviderResponse { get; init; }
}

public sealed record EmailSendResult
{
    public required OutboundEmailAttemptStatus Status { get; init; }

    public string? ProviderCode { get; init; }

    public string? ProviderResponse { get; init; }

    public static EmailSendResult Success(string? providerCode = null, string? providerResponse = null)
    {
        return new EmailSendResult
        {
            Status = OutboundEmailAttemptStatus.Succeeded,
            ProviderCode = providerCode,
            ProviderResponse = providerResponse
        };
    }

    public static EmailSendResult Failed(
        OutboundEmailAttemptStatus status,
        string? providerCode,
        string? providerResponse)
    {
        return new EmailSendResult
        {
            Status = status,
            ProviderCode = providerCode,
            ProviderResponse = providerResponse
        };
    }
}
