using QbReclass.Core.Adapters;
using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>Result of planning a preview: the candidate rows plus the totals the UI displays.</summary>
public sealed record PlanResult
{
    public required IReadOnlyList<CandidateLine> Candidates { get; init; }

    /// <summary>Transactions examined, whether or not any line matched.</summary>
    public int TransactionsExamined { get; init; }

    /// <summary>Candidates that can never be written because their type or shape is unsupported.</summary>
    public int UnsupportedCount => Candidates.Count(c => !c.IsSelectable);

    public int MatchCount => Candidates.Count;

    public decimal TotalAmount => Candidates.Sum(c => c.LineAmount);

    public int ReconciledCount => Candidates.Count(c => c.Cleared is ClearedStatus.Cleared or ClearedStatus.Reconciled);

    /// <summary>
    /// True when QuickBooks did not report a cleared status for any candidate, so the reconciled
    /// count cannot be trusted (spec section 9 says to show the count only when reliably available).
    /// </summary>
    public bool ClearedStatusUnavailable =>
        Candidates.Count > 0 && Candidates.All(c => c.Cleared == ClearedStatus.Unknown);
}

/// <summary>
/// Converts a job rule and a stream of queried transactions into immutable proposed changes
/// (spec section 12, "ReclassificationPlanner").
/// </summary>
/// <remarks>
/// This class performs no I/O and changes nothing. It is the read-only heart of the preview stage
/// required by FR-002 and section 6 step 4.
/// </remarks>
public sealed class ReclassificationPlanner
{
    private readonly AdapterRegistry _registry;

    public ReclassificationPlanner(AdapterRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Produces one candidate per transaction line that matches the rule. A transaction with three
    /// matching lines produces three rows, which is what FR-008 requires.
    /// </summary>
    public PlanResult Plan(Job job, IEnumerable<TransactionSnapshot> transactions)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(transactions);

        var candidates = new List<CandidateLine>();
        var examined = 0;

        foreach (var snapshot in transactions)
        {
            examined++;

            if (!MatchesTransactionFilters(snapshot, job.Filters))
            {
                continue;
            }

            var adapter = _registry.Get(snapshot.TxnType);
            var support = adapter.TypeSupport == ModificationSupport.Supported
                ? adapter.EvaluateRecord(snapshot)
                : new RecordSupport(adapter.TypeSupport, adapter.TypeUnsupportedReason);

            var snapshotHash = ProtectedFields.ComputeSnapshotHash(snapshot);

            var matchingLines = snapshot.ExpenseLines
                .Where(line => IsSourceAccount(line, job.SourceAccount))
                .Where(line => MatchesLineFilters(line, snapshot.Memo, job.Filters))
                .ToList();

            foreach (var line in matchingLines)
            {
                candidates.Add(BuildCandidate(job, snapshot, line, support, snapshotHash, matchingLines.Count));
            }
        }

        return new PlanResult
        {
            Candidates = candidates,
            TransactionsExamined = examined,
        };
    }

    private static CandidateLine BuildCandidate(
        Job job,
        TransactionSnapshot snapshot,
        TransactionLineSnapshot line,
        RecordSupport support,
        string snapshotHash,
        int matchingLineCount)
    {
        var warnings = new List<CandidateWarning>();

        if (snapshot.IsReconciledOrCleared)
        {
            warnings.Add(CandidateWarning.Reconciled(snapshot.Cleared));
        }

        if (line.HasBeenBilled)
        {
            warnings.Add(CandidateWarning.Billed());
        }

        if (snapshot.Lines.Count > 1)
        {
            warnings.Add(CandidateWarning.MultiLine(snapshot.Lines.Count));
        }

        if (matchingLineCount > 1)
        {
            warnings.Add(CandidateWarning.MultipleTargets(matchingLineCount));
        }

        if (!support.CanWrite && support.Reason is not null)
        {
            warnings.Add(CandidateWarning.Unsupported(support.Reason));
        }

        return new CandidateLine
        {
            CandidateId = MakeCandidateId(job.JobId, snapshot.TxnId, line.TxnLineId),
            JobId = job.JobId,
            TxnId = snapshot.TxnId,
            TxnType = snapshot.TxnType,
            TxnLineId = line.TxnLineId ?? string.Empty,
            LineOrdinal = line.Ordinal,
            EditSequenceAtPreview = snapshot.EditSequence,
            TxnDate = snapshot.TxnDate,
            PayeeName = snapshot.Payee.FullName,
            RefNumber = snapshot.RefNumber,
            Memo = line.Memo ?? snapshot.Memo,
            ClassName = line.Class.FullName,
            LineAmount = line.Amount,
            TxnTotalAmount = snapshot.TotalAmount,
            Cleared = snapshot.Cleared,
            PostingAccountName = snapshot.PostingAccount.FullName,
            CurrentAccount = line.Account,
            ProposedAccount = QbRef.FromAccount(job.DestinationAccount),
            Support = support.Support,
            UnsupportedReason = support.Reason,
            Warnings = warnings,
            SnapshotHash = snapshotHash,
            Status = CandidateStatus.Matched,
        };
    }

    /// <summary>
    /// Candidate identity is derived from job, transaction and line rather than generated, so the
    /// same preview run twice produces the same identifiers and a resumed job recognizes work it
    /// already completed (spec FR-016).
    /// </summary>
    public static string MakeCandidateId(string jobId, string txnId, string? txnLineId) =>
        $"{jobId}:{txnId}:{txnLineId ?? "?"}";

    private static bool IsSourceAccount(TransactionLineSnapshot line, QbAccount source)
    {
        // ListID is authoritative; FullName is only consulted when QuickBooks omitted the ListID,
        // because an account rename would otherwise silently change what the rule matches.
        if (!string.IsNullOrEmpty(line.Account.ListId))
        {
            return string.Equals(line.Account.ListId, source.ListId, StringComparison.Ordinal);
        }

        return string.Equals(line.Account.FullName, source.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTransactionFilters(TransactionSnapshot snapshot, JobFilters filters)
    {
        if (!Contains(snapshot.Payee.FullName, filters.PayeeContains))
        {
            return false;
        }

        if (!Contains(snapshot.RefNumber, filters.RefNumberContains))
        {
            return false;
        }

        if (filters.ClearedStatuses.Count > 0 && !filters.ClearedStatuses.Contains(snapshot.Cleared))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesLineFilters(TransactionLineSnapshot line, string? txnMemo, JobFilters filters)
    {
        var amount = Math.Abs(line.Amount);

        if (filters.MinAmount is { } min && amount < min)
        {
            return false;
        }

        if (filters.MaxAmount is { } max && amount > max)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filters.ClassFullName)
            && !string.Equals(line.Class.FullName, filters.ClassFullName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The memo the user is thinking of may live on the line or on the transaction header, so
        // a match on either keeps the line.
        if (!Contains(line.Memo, filters.MemoContains) && !Contains(txnMemo, filters.MemoContains))
        {
            return false;
        }

        return true;
    }

    private static bool Contains(string? value, string? needle) =>
        string.IsNullOrWhiteSpace(needle)
        || (value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
