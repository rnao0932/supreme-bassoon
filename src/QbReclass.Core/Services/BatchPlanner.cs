using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>
/// Groups approved candidate lines into batches and produces the summary the user must read before
/// approving one (spec sections 6 step 6, 9 and FR-010).
/// </summary>
public sealed class BatchPlanner
{
    /// <summary>
    /// Splits selected candidates into batches of at most <see cref="Job.BatchSize"/> lines.
    /// </summary>
    /// <remarks>
    /// Lines belonging to one transaction are never split across batches. The write path applies a
    /// transaction in a single modification request, so a split would leave the second batch
    /// holding candidates whose EditSequence the first batch had already moved.
    /// </remarks>
    public IReadOnlyList<Batch> SplitIntoBatches(Job job, IReadOnlyList<CandidateLine> selected)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(selected);

        var eligible = selected
            .Where(c => c.IsSelectable && !c.IsTerminal)
            .ToList();

        var byTransaction = eligible
            .GroupBy(c => c.TxnId, StringComparer.Ordinal)
            .OrderBy(g => g.Min(c => c.TxnDate))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var batches = new List<Batch>();
        var current = new List<CandidateLine>();
        var sequence = 1;

        foreach (var group in byTransaction)
        {
            var lines = group.ToList();

            if (current.Count > 0 && current.Count + lines.Count > job.BatchSize)
            {
                batches.Add(BuildBatch(job, current, sequence++));
                current = [];
            }

            current.AddRange(lines);

            // A single transaction with more lines than the batch size still travels as one batch;
            // splitting it is not an option, so the batch is allowed to exceed the target.
            if (current.Count >= job.BatchSize)
            {
                batches.Add(BuildBatch(job, current, sequence++));
                current = [];
            }
        }

        if (current.Count > 0)
        {
            batches.Add(BuildBatch(job, current, sequence));
        }

        return batches;
    }

    private static Batch BuildBatch(Job job, IReadOnlyList<CandidateLine> candidates, int sequence) => new()
    {
        BatchId = $"{job.JobId}-B{sequence:D4}",
        JobId = job.JobId,
        Sequence = sequence,
        CandidateIds = candidates.Select(c => c.CandidateId).ToList(),
        TransactionCount = candidates.Select(c => c.TxnId).Distinct(StringComparer.Ordinal).Count(),
        TotalAmount = candidates.Sum(c => c.LineAmount),
        ReconciledCount = candidates.Count(c => c.Cleared is ClearedStatus.Cleared or ClearedStatus.Reconciled),
        WarningCount = candidates.Count(c => c.Warnings.Count > 0),
        BackupConfirmedAt = job.BackupConfirmedAt,
        Status = BatchStatus.Pending,
    };

    /// <summary>
    /// Builds the approval screen's content. Every item listed in spec section 9 appears here, so
    /// the UI cannot omit one by accident.
    /// </summary>
    public BatchApprovalSummary Summarize(Job job, Batch batch, IReadOnlyList<CandidateLine> candidates)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(candidates);

        var inBatch = candidates
            .Where(c => batch.CandidateIds.Contains(c.CandidateId, StringComparer.Ordinal))
            .ToList();

        return new BatchApprovalSummary
        {
            Company = job.Company,
            RuleDescription = job.RuleDescription,
            SourceAccount = job.SourceAccount.FullName,
            DestinationAccount = job.DestinationAccount.FullName,
            FromDate = job.FromDate,
            ToDate = job.ToDate,
            TransactionTypes = job.TransactionTypes,
            TransactionCount = inBatch.Select(c => c.TxnId).Distinct(StringComparer.Ordinal).Count(),
            LineCount = inBatch.Count,
            TotalAmount = inBatch.Sum(c => c.LineAmount),
            ReconciledCount = inBatch.Count(c => c.Cleared is ClearedStatus.Cleared or ClearedStatus.Reconciled),
            WarningCount = inBatch.Count(c => c.Warnings.Count > 0),
            UnsupportedCount = inBatch.Count(c => !c.IsSelectable),
            BackupConfirmedAt = job.BackupConfirmedAt,
        };
    }

    /// <summary>
    /// Marks a batch approved. The caller must have shown the user the corresponding
    /// <see cref="BatchApprovalSummary"/> and received an explicit action.
    /// </summary>
    public Batch Approve(Batch batch, string approvedBy)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.BackupConfirmedAt is null)
        {
            throw new InvalidOperationException(
                "A batch cannot be approved before a current QuickBooks backup has been confirmed.");
        }

        if (batch.CandidateCount == 0)
        {
            throw new InvalidOperationException("A batch with no candidates cannot be approved.");
        }

        return batch with
        {
            Status = BatchStatus.Approved,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = approvedBy,
        };
    }
}
