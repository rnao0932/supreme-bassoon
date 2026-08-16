namespace QbReclass.Core.Model;

/// <summary>
/// Durable record of one write attempt (spec section 13 "ExecutionResult", section 10 "Logging").
/// Every attempt produces one of these, including skips.
/// </summary>
public sealed record ExecutionResult
{
    public required string CandidateId { get; init; }
    public required string BatchId { get; init; }
    public required string JobId { get; init; }
    public required string TxnId { get; init; }
    public required string TxnLineId { get; init; }

    public DateTimeOffset AttemptedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>qbXML request type used, e.g. "CreditCardChargeModRq". Empty for a skip.</summary>
    public string? RequestType { get; init; }

    public ExecutionOutcome Outcome { get; init; }

    /// <summary>qbXML <c>statusCode</c> returned by QuickBooks, when a request was sent.</summary>
    public int? StatusCode { get; init; }

    /// <summary>qbXML <c>statusMessage</c> returned by QuickBooks, verbatim.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Reason for a skip, or the utility-side explanation of a failure.</summary>
    public string? Detail { get; init; }

    /// <summary>Read-back verification outcome. Null when no write was attempted.</summary>
    public VerificationReport? Verification { get; init; }

    /// <summary>Milliseconds spent in the QuickBooks round trip, for the performance log.</summary>
    public long ElapsedMs { get; init; }

    public bool IsSuccess => Outcome == ExecutionOutcome.Success;

    /// <summary>
    /// True when this result must halt the batch regardless of stop policy. A verification
    /// mismatch means QuickBooks holds a state the utility did not intend (spec section 15).
    /// </summary>
    public bool ForcesHalt => Outcome == ExecutionOutcome.VerificationMismatch;

    public string Describe() => Outcome switch
    {
        ExecutionOutcome.Success => $"reclassified ({StatusCode}: {StatusMessage})",
        ExecutionOutcome.Skipped => $"skipped: {Detail}",
        ExecutionOutcome.Failed => $"failed ({StatusCode}: {StatusMessage}) {Detail}".TrimEnd(),
        ExecutionOutcome.VerificationMismatch => $"VERIFICATION MISMATCH: {Verification?.Describe() ?? Detail}",
        _ => Outcome.ToString(),
    };
}

/// <summary>
/// Before/after field capture retained for the audit export (spec section 13 "AuditSnapshot").
/// </summary>
public sealed record AuditSnapshot
{
    public required string CandidateId { get; init; }
    public required string TxnId { get; init; }

    /// <summary>Normalized transaction state captured at preflight, serialized as JSON.</summary>
    public required string BeforeJson { get; init; }

    /// <summary>Normalized transaction state re-queried after the write, serialized as JSON.</summary>
    public string? AfterJson { get; init; }

    public required string BeforeAccount { get; init; }
    public string? AfterAccount { get; init; }

    /// <summary>Rendered protected-field comparison, empty when nothing unexpected changed.</summary>
    public string ProtectedFieldComparison { get; init; } = string.Empty;

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Aggregate outcome of one executed batch, shown on the results screen (spec section 6 step 11).</summary>
public sealed record BatchOutcome
{
    public required string BatchId { get; init; }
    public required IReadOnlyList<ExecutionResult> Results { get; init; }

    public int SucceededCount => Results.Count(r => r.Outcome == ExecutionOutcome.Success);
    public int SkippedCount => Results.Count(r => r.Outcome == ExecutionOutcome.Skipped);
    public int FailedCount => Results.Count(r => r.Outcome == ExecutionOutcome.Failed);
    public int MismatchCount => Results.Count(r => r.Outcome == ExecutionOutcome.VerificationMismatch);

    /// <summary>True when the batch stopped before attempting every candidate.</summary>
    public bool Halted { get; init; }

    public string? HaltReason { get; init; }

    /// <summary>Candidates never attempted because the batch halted.</summary>
    public IReadOnlyList<string> UnattemptedCandidateIds { get; init; } = [];

    public string Summary =>
        $"{SucceededCount} succeeded, {SkippedCount} skipped, {FailedCount} failed, "
        + $"{MismatchCount} verification mismatch"
        + (Halted ? $" - HALTED: {HaltReason}" : string.Empty);
}
