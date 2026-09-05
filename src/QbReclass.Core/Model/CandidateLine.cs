namespace QbReclass.Core.Model;

/// <summary>
/// A warning attached to a candidate. Warnings never block on their own; they are surfaced in the
/// preview grid and counted on the approval screen (spec section 9).
/// </summary>
public sealed record CandidateWarning(string Code, string Message)
{
    public static CandidateWarning Reconciled(ClearedStatus status) =>
        new("RECONCILED", $"Transaction is {status.ToString().ToLowerInvariant()} in QuickBooks; treated as high risk.");

    public static CandidateWarning Paid() =>
        new("PAID", "Bill has been paid; reclassifying changes what a settled payment was for.");

    public static CandidateWarning Billed() =>
        new("BILLED", "Line is marked HasBeenBilled; reclassifying may affect customer invoicing.");

    public static CandidateWarning MultiLine(int lineCount) =>
        new("MULTILINE", $"Transaction has {lineCount} lines; all lines are resubmitted to preserve them.");

    public static CandidateWarning MultipleTargets(int count) =>
        new("MULTI_TARGET", $"{count} lines on this transaction are selected for reclassification.");

    public static CandidateWarning Unsupported(string reason) =>
        new("UNSUPPORTED", reason);
}

/// <summary>
/// One reclassifiable transaction line that matched the job rule. The preview grid shows one row
/// per candidate, not one row per transaction (spec FR-008).
/// </summary>
public sealed record CandidateLine
{
    public required string CandidateId { get; init; }
    public required string JobId { get; init; }

    public required string TxnId { get; init; }
    public required TransactionType TxnType { get; init; }

    /// <summary>Line within the transaction to reclassify.</summary>
    public required string TxnLineId { get; init; }

    /// <summary>Position of the target line within the transaction, for display and ordering.</summary>
    public int LineOrdinal { get; init; }

    /// <summary>EditSequence observed at preview time. Compared again at preflight (spec FR-011/012).</summary>
    public required string EditSequenceAtPreview { get; init; }

    public DateOnly TxnDate { get; init; }
    public string? PayeeName { get; init; }
    public string? RefNumber { get; init; }
    public string? Memo { get; init; }
    public string? ClassName { get; init; }
    public decimal LineAmount { get; init; }
    public decimal TxnTotalAmount { get; init; }
    public ClearedStatus Cleared { get; init; } = ClearedStatus.Unknown;
    public string? PostingAccountName { get; init; }

    public required QbRef CurrentAccount { get; init; }
    public required QbRef ProposedAccount { get; init; }

    /// <summary>
    /// Whether this record can be written at all. <see cref="ModificationSupport.NotSupported"/>
    /// and <see cref="ModificationSupport.UnsupportedRecordShape"/> are shown in preview and never
    /// written (spec FR-018).
    /// </summary>
    public ModificationSupport Support { get; init; } = ModificationSupport.Supported;

    /// <summary>Explanation shown next to an unsupported record. Null when supported.</summary>
    public string? UnsupportedReason { get; init; }

    public IReadOnlyList<CandidateWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Hash over the fields the reclassification must not disturb, captured at preview. Recomputed
    /// at preflight to detect an edit QuickBooks did not reflect in EditSequence.
    /// </summary>
    public required string SnapshotHash { get; init; }

    public CandidateStatus Status { get; init; } = CandidateStatus.Matched;

    /// <summary>True when the user has selected this row for inclusion in a batch.</summary>
    public bool IsSelected { get; init; }

    /// <summary>Batch this candidate belongs to, once approved.</summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// qbXML request type recorded immediately before the request was handed to QuickBooks. Set
    /// alongside <see cref="CandidateStatus.Submitted"/> and read by restart reconciliation.
    /// </summary>
    public string? SubmittedRequestType { get; init; }

    /// <summary>True when the row may legally be selected for writing.</summary>
    public bool IsSelectable => Support == ModificationSupport.Supported;

    /// <summary>True when the candidate has reached a terminal state and must never be retried.</summary>
    public bool IsTerminal => Status is CandidateStatus.Verified or CandidateStatus.Skipped or CandidateStatus.Failed;

    public string StatusLabel => Support switch
    {
        ModificationSupport.NotSupported => "Unsupported type",
        ModificationSupport.UnsupportedRecordShape => "Unsupported record",
        _ => Status.ToString(),
    };
}
