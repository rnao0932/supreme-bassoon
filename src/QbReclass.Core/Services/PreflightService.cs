using QbReclass.Core.Adapters;
using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>Why a preflight refused a record.</summary>
public enum PreflightFailure
{
    None = 0,

    /// <summary>The transaction no longer exists in QuickBooks.</summary>
    TransactionMissing,

    /// <summary>EditSequence moved: someone changed the record after the preview (spec FR-012).</summary>
    Stale,

    /// <summary>EditSequence matched but a protected field differs from the preview snapshot.</summary>
    SnapshotDrift,

    /// <summary>The target line is gone.</summary>
    LineMissing,

    /// <summary>The target line no longer points at the source account; the rule no longer applies.</summary>
    SourceAccountChanged,

    /// <summary>The line amount changed since preview.</summary>
    AmountChanged,

    /// <summary>The destination account was deleted, made inactive, or is not expense-side.</summary>
    DestinationAccountInvalid,

    /// <summary>The record's type or shape has no supported in-place modification.</summary>
    Unsupported,

    /// <summary>The record is already reclassified; treat as complete, not as work to redo.</summary>
    AlreadyAtDestination,
}

/// <summary>
/// Outcome of the final read before a write. On success it carries the freshly read snapshot -
/// including the current EditSequence - which is what the modification request must use.
/// </summary>
public sealed record PreflightOutcome
{
    public required string CandidateId { get; init; }
    public PreflightFailure Failure { get; init; } = PreflightFailure.None;
    public string? Detail { get; init; }

    /// <summary>Transaction exactly as QuickBooks holds it right now. Null when preflight failed.</summary>
    public TransactionSnapshot? Current { get; init; }

    public bool Passed => Failure == PreflightFailure.None;

    /// <summary>
    /// True when the record already holds the intended state. Not a failure: the batch records it
    /// as complete so a restart does not attempt the same change twice (spec FR-016).
    /// </summary>
    public bool AlreadyDone => Failure == PreflightFailure.AlreadyAtDestination;

    public static PreflightOutcome Fail(string candidateId, PreflightFailure failure, string detail) =>
        new() { CandidateId = candidateId, Failure = failure, Detail = detail };
}

/// <summary>
/// The last read before a write (spec section 12 "PreflightService", section 14 steps 2-4).
/// </summary>
/// <remarks>
/// Preflight exists so the utility never modifies a record on the strength of a preview that may be
/// minutes or days old. It is not an optimization and must never be skipped for throughput
/// (spec section 16).
/// </remarks>
public sealed class PreflightService
{
    private readonly QueryService _query;
    private readonly AdapterRegistry _registry;

    public PreflightService(QueryService query, AdapterRegistry registry)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Re-reads a candidate's transaction and confirms every precondition for the write.
    /// </summary>
    /// <param name="job">Job the candidate belongs to; supplies source and destination accounts.</param>
    /// <param name="candidate">Candidate as captured at preview.</param>
    /// <param name="destinationAccount">
    /// Destination account re-read from QuickBooks in this batch, so an account made inactive after
    /// the preview is caught. Pass null to have preflight read it.
    /// </param>
    public PreflightOutcome Check(Job job, CandidateLine candidate, QbAccount? destinationAccount = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(candidate);

        var adapter = _registry.Get(candidate.TxnType);
        if (adapter.TypeSupport != ModificationSupport.Supported)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId, PreflightFailure.Unsupported, adapter.TypeUnsupportedReason ?? "Unsupported type.");
        }

        var destination = destinationAccount ?? _query.GetAccount(job.DestinationAccount.ListId);
        if (destination is null)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.DestinationAccountInvalid,
                $"Destination account '{job.DestinationAccount.FullName}' no longer exists in QuickBooks.");
        }

        if (!destination.IsActive)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.DestinationAccountInvalid,
                $"Destination account '{destination.FullName}' has been made inactive since the preview.");
        }

        if (!destination.IsExpenseSide)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.DestinationAccountInvalid,
                $"Destination account '{destination.FullName}' is of type '{destination.AccountType}' and "
                + "cannot carry an expense line.");
        }

        var current = _query.GetTransaction(candidate.TxnType, candidate.TxnId);
        if (current is null)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.TransactionMissing,
                $"Transaction {candidate.TxnId} no longer exists in QuickBooks.");
        }

        var line = current.FindLine(candidate.TxnLineId);
        if (line is null)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.LineMissing,
                $"Line {candidate.TxnLineId} is no longer present on transaction {candidate.TxnId}.");
        }

        // Already-at-destination is checked before staleness. A record this utility successfully
        // modified in an earlier run is legitimately stale relative to the preview; treating that
        // as a conflict would strand completed work in a permanent error state.
        if (string.Equals(line.Account.ListId, destination.ListId, StringComparison.Ordinal))
        {
            return new PreflightOutcome
            {
                CandidateId = candidate.CandidateId,
                Failure = PreflightFailure.AlreadyAtDestination,
                Detail = $"Line already posts to '{destination.FullName}'. No change required.",
                Current = current,
            };
        }

        if (!string.Equals(current.EditSequence, candidate.EditSequenceAtPreview, StringComparison.Ordinal))
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.Stale,
                $"Transaction {candidate.TxnId} changed since the preview "
                + $"(EditSequence '{candidate.EditSequenceAtPreview}' -> '{current.EditSequence}'). "
                + "Re-run the preview and review this record before changing it.");
        }

        // EditSequence is QuickBooks' own guarantee, but the snapshot hash is checked as well so a
        // change QuickBooks did not version is still caught.
        var currentHash = ProtectedFields.ComputeSnapshotHash(current);
        if (!string.Equals(currentHash, candidate.SnapshotHash, StringComparison.Ordinal))
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.SnapshotDrift,
                $"Transaction {candidate.TxnId} reports an unchanged EditSequence but its contents "
                + "differ from the preview. Re-run the preview before changing it.");
        }

        if (!string.Equals(line.Account.ListId, job.SourceAccount.ListId, StringComparison.Ordinal))
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.SourceAccountChanged,
                $"Line now posts to '{line.Account}' rather than the rule's source account "
                + $"'{job.SourceAccount.FullName}'.");
        }

        if (line.Amount != candidate.LineAmount)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId,
                PreflightFailure.AmountChanged,
                $"Line amount changed from {candidate.LineAmount:0.00} to {line.Amount:0.00} since the preview.");
        }

        var support = adapter.EvaluateRecord(current);
        if (!support.CanWrite)
        {
            return PreflightOutcome.Fail(
                candidate.CandidateId, PreflightFailure.Unsupported, support.Reason ?? "Unsupported record.");
        }

        return new PreflightOutcome { CandidateId = candidate.CandidateId, Current = current };
    }
}
