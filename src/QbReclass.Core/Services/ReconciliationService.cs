using QbReclass.Core.Audit;
using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>How an interrupted write turned out once QuickBooks was asked.</summary>
public enum ReconciliationVerdict
{
    /// <summary>The change did land. The candidate is complete and must not be reapplied.</summary>
    Applied = 0,

    /// <summary>The change never landed. The candidate is returned to the pool, deselected.</summary>
    NotApplied,

    /// <summary>The record is in neither state. Needs a human.</summary>
    Indeterminate,
}

/// <summary>One reconciled candidate.</summary>
public sealed record ReconciliationItem(
    string CandidateId,
    string TxnId,
    ReconciliationVerdict Verdict,
    string Detail);

/// <summary>Outcome of a reconciliation sweep at startup.</summary>
public sealed record ReconciliationOutcome
{
    public required IReadOnlyList<ReconciliationItem> Items { get; init; }

    public int AppliedCount => Items.Count(i => i.Verdict == ReconciliationVerdict.Applied);
    public int NotAppliedCount => Items.Count(i => i.Verdict == ReconciliationVerdict.NotApplied);
    public int IndeterminateCount => Items.Count(i => i.Verdict == ReconciliationVerdict.Indeterminate);

    public bool NeedsAttention => IndeterminateCount > 0;

    public string Summary => Items.Count == 0
        ? "No interrupted writes to reconcile."
        : $"{Items.Count} interrupted write(s): {AppliedCount} had been applied, "
          + $"{NotAppliedCount} had not, {IndeterminateCount} need review.";
}

/// <summary>
/// Resolves writes that were in flight when the application stopped
/// (spec FR-016, section 10 "Idempotency", section 18 restart test).
/// </summary>
/// <remarks>
/// A candidate left in <see cref="CandidateStatus.Submitted"/> means the utility handed a request
/// to QuickBooks and never recorded what came back. QuickBooks is the only authority on what
/// actually happened, so this service asks it rather than guessing. Nothing is rewritten here: a
/// change that did not land is returned to the pool deselected, so re-applying it requires a fresh
/// preview, selection and batch approval.
/// </remarks>
public sealed class ReconciliationService
{
    private readonly QueryService _query;
    private readonly IAuditStore _store;
    private readonly Action<string>? _log;

    public ReconciliationService(QueryService query, IAuditStore store, Action<string>? log = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>Reconciles every in-flight candidate on a job. Safe to call when there are none.</summary>
    public ReconciliationOutcome Reconcile(Job job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var pending = _store.GetCandidatesByStatus(job.JobId, CandidateStatus.Submitted);
        var items = new List<ReconciliationItem>();

        foreach (var candidate in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(ReconcileOne(job, candidate));
        }

        var outcome = new ReconciliationOutcome { Items = items };
        _log?.Invoke(outcome.Summary);
        return outcome;
    }

    private ReconciliationItem ReconcileOne(Job job, CandidateLine candidate)
    {
        var current = _query.GetTransaction(candidate.TxnType, candidate.TxnId);

        if (current is null)
        {
            return Record(
                job,
                candidate,
                ReconciliationVerdict.Indeterminate,
                $"Transaction {candidate.TxnId} no longer exists. It was being modified when the "
                + "application stopped. Review this transaction in QuickBooks before continuing.",
                CandidateStatus.Failed,
                ExecutionOutcome.VerificationMismatch);
        }

        var line = current.FindLine(candidate.TxnLineId);

        if (line is null)
        {
            return Record(
                job,
                candidate,
                ReconciliationVerdict.Indeterminate,
                $"Line {candidate.TxnLineId} is no longer present on transaction {candidate.TxnId}. "
                + "Review this transaction in QuickBooks before continuing.",
                CandidateStatus.Failed,
                ExecutionOutcome.VerificationMismatch);
        }

        if (string.Equals(line.Account.ListId, job.DestinationAccount.ListId, StringComparison.Ordinal))
        {
            return Record(
                job,
                candidate,
                ReconciliationVerdict.Applied,
                $"Change had been applied before the interruption; line posts to "
                + $"'{job.DestinationAccount.FullName}'. Marked complete and will not be reapplied.",
                CandidateStatus.Verified,
                ExecutionOutcome.Success);
        }

        if (string.Equals(line.Account.ListId, job.SourceAccount.ListId, StringComparison.Ordinal))
        {
            return Record(
                job,
                candidate,
                ReconciliationVerdict.NotApplied,
                "Change had not been applied before the interruption. The line is unchanged and has "
                + "been returned to the preview, deselected. Re-select and approve a batch to retry.",
                CandidateStatus.Matched,
                ExecutionOutcome.Skipped,
                deselect: true);
        }

        return Record(
            job,
            candidate,
            ReconciliationVerdict.Indeterminate,
            $"Line now posts to '{line.Account}', which is neither the rule's source account nor its "
            + "destination. Review this transaction in QuickBooks before continuing.",
            CandidateStatus.Failed,
            ExecutionOutcome.VerificationMismatch);
    }

    private ReconciliationItem Record(
        Job job,
        CandidateLine candidate,
        ReconciliationVerdict verdict,
        string detail,
        CandidateStatus newStatus,
        ExecutionOutcome outcome,
        bool deselect = false)
    {
        _store.SaveResult(new ExecutionResult
        {
            CandidateId = candidate.CandidateId,
            BatchId = candidate.BatchId ?? string.Empty,
            JobId = job.JobId,
            TxnId = candidate.TxnId,
            TxnLineId = candidate.TxnLineId,
            RequestType = candidate.SubmittedRequestType,
            Outcome = outcome,
            Detail = $"Restart reconciliation: {detail}",
        });

        _store.UpdateCandidate(candidate with
        {
            Status = newStatus,
            IsSelected = !deselect && candidate.IsSelected && newStatus != CandidateStatus.Matched,
        });

        _log?.Invoke($"{candidate.CandidateId}: {verdict} - {detail}");
        return new ReconciliationItem(candidate.CandidateId, candidate.TxnId, verdict, detail);
    }
}
