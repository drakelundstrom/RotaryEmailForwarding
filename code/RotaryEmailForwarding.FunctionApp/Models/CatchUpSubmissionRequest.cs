namespace RotaryEmailForwarding.FunctionApp.Models;

public sealed record CatchUpSubmissionRequest
{
    public required DateTimeOffset OriginalProcessedOnUtc { get; init; }

    public required InterestFormSubmissionRequest Submission { get; init; }
}

public enum CatchUpSubmissionStatus
{
    Sent,
    AlreadySent,
    NotFound,
    Ambiguous,
    Failed
}

public sealed record CatchUpSubmissionItemResult
{
    public required int Index { get; init; }

    public required CatchUpSubmissionStatus Status { get; init; }

    public string? SubmissionId { get; init; }

    public string? Message { get; init; }
}

public sealed record CatchUpBatchResult
{
    public required IReadOnlyList<CatchUpSubmissionItemResult> Items { get; init; }

    public int Sent => Items.Count(item => item.Status == CatchUpSubmissionStatus.Sent);

    public int AlreadySent => Items.Count(item => item.Status == CatchUpSubmissionStatus.AlreadySent);

    public int NotFound => Items.Count(item => item.Status == CatchUpSubmissionStatus.NotFound);

    public int Ambiguous => Items.Count(item => item.Status == CatchUpSubmissionStatus.Ambiguous);

    public int Failed => Items.Count(item => item.Status == CatchUpSubmissionStatus.Failed);
}
