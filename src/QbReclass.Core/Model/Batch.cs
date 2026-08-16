namespace QbReclass.Core.Model;

/// <summary>
/// A user-approved unit of work. Nothing is written outside an approved batch (spec section 1).
/// </summary>
public sealed record Batch
{
    public required string BatchId { get; init; }
    public required string JobId { get; init; }

    /// <summary>Ordinal of this batch within the job, 1-based, for display.</summary>
    public int Sequence { get; init; }

    public IReadOnlyList<string> CandidateIds { get; init; } = [];

    public int CandidateCount => CandidateIds.Count;

    /// <summary>Distinct transactions touched. Differs from candidate count on multi-line selections.</summary>
    public int TransactionCount { get; init; }

    public decimal TotalAmount { get; init; }
    public int ReconciledCount { get; init; }
    public int WarningCount { get; init; }

    public BatchStatus Status { get; init; } = BatchStatus.Pending;

    /// <summary>Set when the user clicks the explicit approval button (spec section 9).</summary>
    public DateTimeOffset? ApprovedAt { get; init; }

    /// <summary>Local Windows user who approved, recorded for the audit log.</summary>
    public string? ApprovedBy { get; init; }

    /// <summary>Backup confirmation timestamp copied from the job, shown on the approval screen.</summary>
    public DateTimeOffset? BackupConfirmedAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Reason the batch halted early, when it did.</summary>
    public string? HaltReason { get; init; }

    /// <summary>
    /// Unambiguous label for the final action button. The spec forbids "OK" or "Continue"
    /// (section 9).
    /// </summary>
    public string ApprovalButtonText =>
        $"Apply {CandidateCount} Approved Reclassification{(CandidateCount == 1 ? string.Empty : "s")}";
}

/// <summary>
/// Everything the batch approval screen must display before the user commits (spec section 9).
/// </summary>
public sealed record BatchApprovalSummary
{
    public required CompanyIdentity Company { get; init; }
    public required string RuleDescription { get; init; }
    public required string SourceAccount { get; init; }
    public required string DestinationAccount { get; init; }
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }
    public required IReadOnlyList<TransactionType> TransactionTypes { get; init; }

    public int TransactionCount { get; init; }
    public int LineCount { get; init; }
    public decimal TotalAmount { get; init; }
    public int ReconciledCount { get; init; }
    public int WarningCount { get; init; }
    public int UnsupportedCount { get; init; }

    public DateTimeOffset? BackupConfirmedAt { get; init; }

    /// <summary>Fixed statement of scope required by the spec.</summary>
    public string ScopeStatement =>
        $"Only the {LineCount} transaction line{(LineCount == 1 ? string.Empty : "s")} listed in this batch "
        + "will be changed. No other transaction in this company file will be modified.";

    public string ButtonText =>
        $"Apply {LineCount} Approved Reclassification{(LineCount == 1 ? string.Empty : "s")}";
}
