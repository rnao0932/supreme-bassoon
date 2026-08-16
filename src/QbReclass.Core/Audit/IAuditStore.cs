using QbReclass.Core.Model;

namespace QbReclass.Core.Audit;

/// <summary>
/// Durable local record of jobs, candidates, batches and every write attempt
/// (spec section 12 "AuditStore", FR-016, FR-017).
/// </summary>
/// <remarks>
/// The store is the utility's memory across restarts. The ordering guarantee that matters is in
/// <see cref="MarkSubmitted"/>: a candidate is recorded as submitted, durably, <b>before</b> the
/// request reaches QuickBooks. If the process dies mid-write, the record left behind says "outcome
/// unknown", which is exactly the state <see cref="Services.ReconciliationService"/> knows how to
/// resolve. Recording after the write instead would leave a completed change looking untried.
/// </remarks>
public interface IAuditStore : IDisposable
{
    void SaveJob(Job job);

    Job? GetJob(string jobId);

    IReadOnlyList<Job> ListJobs();

    void UpdateJob(Job job);

    /// <summary>Replaces the candidate set for a job. Called once per preview.</summary>
    void SaveCandidates(string jobId, IEnumerable<CandidateLine> candidates);

    IReadOnlyList<CandidateLine> GetCandidates(string jobId);

    IReadOnlyList<CandidateLine> GetCandidatesByStatus(string jobId, CandidateStatus status);

    CandidateLine? GetCandidate(string candidateId);

    void UpdateCandidate(CandidateLine candidate);

    /// <summary>Sets the selection flag on a set of candidates (spec FR-009).</summary>
    void SetSelection(IEnumerable<string> candidateIds, bool isSelected);

    void SaveBatch(Batch batch);

    Batch? GetBatch(string batchId);

    IReadOnlyList<Batch> GetBatches(string jobId);

    /// <summary>
    /// Records, durably, that a modification request is about to be sent. Must return only after
    /// the write has been flushed.
    /// </summary>
    void MarkSubmitted(string candidateId, string batchId, string requestType);

    void SaveResult(ExecutionResult result);

    IReadOnlyList<ExecutionResult> GetResults(string jobId);

    IReadOnlyList<ExecutionResult> GetResultsForBatch(string batchId);

    void SaveSnapshot(AuditSnapshot snapshot);

    IReadOnlyList<AuditSnapshot> GetSnapshots(string jobId);

    AuditSnapshot? GetSnapshot(string candidateId);
}
