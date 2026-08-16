using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using QbReclass.Core.Adapters;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Session;

namespace QbReclass.Core.Services;

/// <summary>Progress callback payload for the UI's progress screen.</summary>
public sealed record ExecutionProgress(
    int Completed,
    int Total,
    string CurrentTxnId,
    ExecutionResult? LastResult);

/// <summary>
/// The only class in the utility that writes to QuickBooks
/// (spec section 12 "ExecutionService", section 14).
/// </summary>
/// <remarks>
/// <para>
/// Writes are issued one transaction at a time, each preceded by its own preflight read and
/// followed by its own read-back verification. That is slower than batching requests into a single
/// envelope, and it is chosen deliberately: it is what makes failure isolation (FR-015), stop
/// control and per-record audit possible.
/// </para>
/// <para>
/// Candidates that share a TxnID are applied in a single modification request. Issuing two
/// requests against one transaction would guarantee that the second failed on a stale
/// EditSequence, and re-reading between them would mean writing to a record whose state the user
/// never approved.
/// </para>
/// </remarks>
public sealed class ExecutionService
{
    private readonly IQbSession _session;
    private readonly QueryService _query;
    private readonly PreflightService _preflight;
    private readonly VerificationService _verification;
    private readonly AdapterRegistry _registry;
    private readonly IAuditStore _store;
    private readonly Action<string>? _log;

    public ExecutionService(
        IQbSession session,
        QueryService query,
        PreflightService preflight,
        VerificationService verification,
        AdapterRegistry registry,
        IAuditStore store,
        Action<string>? log = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log;
    }

    /// <summary>
    /// Executes one approved batch.
    /// </summary>
    /// <param name="job">Job the batch belongs to.</param>
    /// <param name="batch">Batch the user explicitly approved.</param>
    /// <param name="progress">Optional per-record progress callback.</param>
    /// <param name="cancellationToken">
    /// Honoured between transactions, never inside one. That is what "stop after the current
    /// transaction" means in spec section 10.
    /// </param>
    public BatchOutcome Execute(
        Job job,
        Batch batch,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(batch);

        GuardPreconditions(job, batch);

        var candidates = batch.CandidateIds
            .Select(_store.GetCandidate)
            .OfType<CandidateLine>()
            .Where(c => !c.IsTerminal)
            .ToList();

        // One destination read for the whole batch: it is the same account for every record, and
        // re-reading it per record would add a round trip without adding a guarantee.
        var destination = _query.GetAccount(job.DestinationAccount.ListId);

        var groups = candidates
            .GroupBy(c => c.TxnId, StringComparer.Ordinal)
            .ToList();

        var results = new List<ExecutionResult>();
        var unattempted = new List<string>();
        string? haltReason = null;
        var completed = 0;

        foreach (var group in groups)
        {
            if (haltReason is not null)
            {
                unattempted.AddRange(group.Select(c => c.CandidateId));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                haltReason = "Stopped by user after the previous transaction.";
                unattempted.AddRange(group.Select(c => c.CandidateId));
                continue;
            }

            List<ExecutionResult> groupResults;
            try
            {
                groupResults = ExecuteTransaction(job, batch, group.Key, [.. group], destination);
            }
            catch (QbSessionException ex)
            {
                // QuickBooks went away mid-batch. State is already durable; stop and let restart
                // reconciliation sort out anything left in flight (spec section 15).
                _log?.Invoke($"Session lost during batch {batch.BatchId}: {ex.Message}");
                haltReason = $"QuickBooks connection lost: {ex.Message}";
                unattempted.AddRange(group.Select(c => c.CandidateId));
                continue;
            }

            results.AddRange(groupResults);
            completed += groupResults.Count;

            var last = groupResults[^1];
            progress?.Report(new ExecutionProgress(completed, candidates.Count, group.Key, last));

            haltReason = DecideHalt(job, groupResults);
        }

        var outcome = new BatchOutcome
        {
            BatchId = batch.BatchId,
            Results = results,
            Halted = haltReason is not null,
            HaltReason = haltReason,
            UnattemptedCandidateIds = unattempted,
        };

        _store.SaveBatch(batch with
        {
            Status = haltReason is null ? BatchStatus.Completed : BatchStatus.Halted,
            CompletedAt = DateTimeOffset.UtcNow,
            HaltReason = haltReason,
        });

        _log?.Invoke($"Batch {batch.BatchId}: {outcome.Summary}");
        return outcome;
    }

    /// <summary>
    /// Preflights, writes and verifies every selected line on a single transaction.
    /// </summary>
    private List<ExecutionResult> ExecuteTransaction(
        Job job,
        Batch batch,
        string txnId,
        IReadOnlyList<CandidateLine> candidates,
        QbAccount? destination)
    {
        var results = new List<ExecutionResult>();
        var stopwatch = Stopwatch.StartNew();

        // Preflight every line first. If any line on the transaction fails preflight, none of them
        // is written: the request is per-transaction, so a partial application is not available.
        var outcomes = new Dictionary<string, PreflightOutcome>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            outcomes[candidate.CandidateId] = _preflight.Check(job, candidate, destination);
        }

        var blocked = outcomes.Values.FirstOrDefault(o => !o.Passed && !o.AlreadyDone);
        if (blocked is not null)
        {
            foreach (var candidate in candidates)
            {
                var own = outcomes[candidate.CandidateId];
                var detail = own.Passed
                    ? $"Not attempted: another selected line on transaction {txnId} failed preflight "
                      + $"({blocked.Failure}: {blocked.Detail})"
                    : own.Detail;

                results.Add(RecordSkip(job, batch, candidate, own.Failure, detail));
            }

            return results;
        }

        var writable = candidates.Where(c => !outcomes[c.CandidateId].AlreadyDone).ToList();

        foreach (var candidate in candidates.Where(c => outcomes[c.CandidateId].AlreadyDone))
        {
            results.Add(RecordAlreadyDone(job, batch, candidate, outcomes[candidate.CandidateId]));
        }

        if (writable.Count == 0)
        {
            return results;
        }

        var before = outcomes[writable[0].CandidateId].Current!;
        var adapter = _registry.Get(before.TxnType);
        var targetLineIds = writable.Select(c => c.TxnLineId).ToList();
        var destinationRef = destination is not null
            ? QbRef.FromAccount(destination)
            : QbRef.FromAccount(job.DestinationAccount);

        XElement request;
        try
        {
            request = adapter.BuildReclassification(before, targetLineIds, destinationRef);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            foreach (var candidate in writable)
            {
                results.Add(RecordSkip(job, batch, candidate, PreflightFailure.Unsupported, ex.Message));
            }

            return results;
        }

        // Persist the intent before the request leaves. Everything after this point may be
        // interrupted; nothing after this point may be lost.
        foreach (var candidate in writable)
        {
            _store.MarkSubmitted(candidate.CandidateId, batch.BatchId, adapter.ModRequestName!);
            _store.SaveSnapshot(new AuditSnapshot
            {
                CandidateId = candidate.CandidateId,
                TxnId = txnId,
                BeforeJson = JsonSerializer.Serialize(before),
                BeforeAccount = candidate.CurrentAccount.ToString(),
            });
        }

        var response = _query.Send(request);
        stopwatch.Stop();

        if (!response.Status.IsOk)
        {
            foreach (var candidate in writable)
            {
                results.Add(RecordFailure(job, batch, candidate, adapter.ModRequestName!, response.Status, stopwatch.ElapsedMilliseconds));
            }

            return results;
        }

        // Read the transaction back rather than trusting the echo in the response
        // (spec section 14 step 8).
        var verification = _verification.Verify(before, targetLineIds, destinationRef.ListId!);
        var after = _query.GetTransaction(before.TxnType, txnId);

        foreach (var candidate in writable)
        {
            _store.SaveSnapshot(new AuditSnapshot
            {
                CandidateId = candidate.CandidateId,
                TxnId = txnId,
                BeforeJson = JsonSerializer.Serialize(before),
                AfterJson = after is null ? null : JsonSerializer.Serialize(after),
                BeforeAccount = candidate.CurrentAccount.ToString(),
                AfterAccount = after?.FindLine(candidate.TxnLineId)?.Account.ToString(),
                ProtectedFieldComparison = string.Join("; ", verification.Differences),
            });

            var result = new ExecutionResult
            {
                CandidateId = candidate.CandidateId,
                BatchId = batch.BatchId,
                JobId = job.JobId,
                TxnId = txnId,
                TxnLineId = candidate.TxnLineId,
                RequestType = adapter.ModRequestName,
                Outcome = verification.Passed ? ExecutionOutcome.Success : ExecutionOutcome.VerificationMismatch,
                StatusCode = response.Status.Code,
                StatusMessage = response.Status.Message,
                Verification = verification,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Detail = verification.Passed ? null : verification.Describe(),
            };

            _store.SaveResult(result);
            _store.UpdateCandidate(candidate with
            {
                Status = verification.Passed ? CandidateStatus.Verified : CandidateStatus.Failed,
                BatchId = batch.BatchId,

                // The candidate is finished either way; clearing the selection keeps it out of the
                // next batch and out of the selected totals on screen.
                IsSelected = false,
            });

            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Decides whether the batch continues. A verification mismatch always stops it; other
    /// failures stop it unless the user chose to continue past isolated failures
    /// (spec section 15, FR-015).
    /// </summary>
    private static string? DecideHalt(Job job, IReadOnlyList<ExecutionResult> results)
    {
        var mismatch = results.FirstOrDefault(r => r.ForcesHalt);
        if (mismatch is not null)
        {
            return $"Verification mismatch on transaction {mismatch.TxnId}: {mismatch.Verification?.Describe()}";
        }

        if (job.StopPolicy == StopPolicy.ContinueOnIsolatedFailure)
        {
            return null;
        }

        var failure = results.FirstOrDefault(r => r.Outcome == ExecutionOutcome.Failed);
        return failure is null
            ? null
            : $"Transaction {failure.TxnId} failed ({failure.StatusCode}: {failure.StatusMessage}). "
              + "Stop policy is stop-on-first-failure.";
    }

    private void GuardPreconditions(Job job, Batch batch)
    {
        if (job.BackupConfirmedAt is null)
        {
            throw new InvalidOperationException(
                "Write mode requires confirmation that a current QuickBooks backup exists.");
        }

        if (batch.Status != BatchStatus.Approved || batch.ApprovedAt is null)
        {
            throw new InvalidOperationException(
                $"Batch {batch.BatchId} has not been approved. Nothing is written outside an approved batch.");
        }

        if (_session.Info.IsReadOnly)
        {
            throw new InvalidOperationException(
                "The QuickBooks session is read-only. Reconnect with write access before executing a batch.");
        }

        // Re-read company identity rather than trusting the value captured at connect time: the
        // user may have switched company files while the preview was on screen.
        var host = _query.LoadHostInfo();
        var current = _query.LoadCompanyIdentity(host, _session.Info.Company.CompanyFileName);

        if (!job.Company.Matches(current))
        {
            throw new QbCompanyMismatchException(job.Company, current);
        }
    }

    private ExecutionResult RecordSkip(
        Job job,
        Batch batch,
        CandidateLine candidate,
        PreflightFailure failure,
        string? detail)
    {
        var result = new ExecutionResult
        {
            CandidateId = candidate.CandidateId,
            BatchId = batch.BatchId,
            JobId = job.JobId,
            TxnId = candidate.TxnId,
            TxnLineId = candidate.TxnLineId,
            Outcome = ExecutionOutcome.Skipped,
            Detail = $"{failure}: {detail}",
        };

        _store.SaveResult(result);
        _store.UpdateCandidate(candidate with
        {
            Status = CandidateStatus.Skipped,
            BatchId = batch.BatchId,
            IsSelected = false,
        });
        return result;
    }

    private ExecutionResult RecordAlreadyDone(Job job, Batch batch, CandidateLine candidate, PreflightOutcome outcome)
    {
        // Idempotency (spec section 10): a line already at the destination is complete, not work.
        var result = new ExecutionResult
        {
            CandidateId = candidate.CandidateId,
            BatchId = batch.BatchId,
            JobId = job.JobId,
            TxnId = candidate.TxnId,
            TxnLineId = candidate.TxnLineId,
            Outcome = ExecutionOutcome.Skipped,
            Detail = outcome.Detail,
        };

        _store.SaveResult(result);
        _store.UpdateCandidate(candidate with
        {
            Status = CandidateStatus.Verified,
            BatchId = batch.BatchId,
            IsSelected = false,
        });
        return result;
    }

    private ExecutionResult RecordFailure(
        Job job,
        Batch batch,
        CandidateLine candidate,
        string requestType,
        QbStatus status,
        long elapsedMs)
    {
        var result = new ExecutionResult
        {
            CandidateId = candidate.CandidateId,
            BatchId = batch.BatchId,
            JobId = job.JobId,
            TxnId = candidate.TxnId,
            TxnLineId = candidate.TxnLineId,
            RequestType = requestType,
            Outcome = ExecutionOutcome.Failed,
            StatusCode = status.Code,
            StatusMessage = status.Message,
            Detail = $"QuickBooks rejected the modification ({status.Category}).",
            ElapsedMs = elapsedMs,
        };

        _store.SaveResult(result);
        _store.UpdateCandidate(candidate with
        {
            Status = CandidateStatus.Failed,
            BatchId = batch.BatchId,
            IsSelected = false,
        });
        return result;
    }
}
