using System.Globalization;
using System.Text;
using System.Text.Json;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>
/// Writes the audit log out for human review and archival (spec FR-017, section 12 "ExportService").
/// </summary>
/// <remarks>
/// Exports contain financial data and QuickBooks identifiers and are to be handled accordingly
/// (spec section 17). Nothing here reaches the network; the caller chooses the destination path.
/// </remarks>
public sealed class ExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IAuditStore _store;

    public ExportService(IAuditStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Writes one CSV row per write attempt, carrying before and after classification alongside the
    /// QuickBooks identifiers needed to find the record again.
    /// </summary>
    public void ExportCsv(string jobId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var job = _store.GetJob(jobId)
            ?? throw new InvalidOperationException($"Unknown job '{jobId}'.");

        var candidates = _store.GetCandidates(jobId).ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var snapshots = _store.GetSnapshots(jobId).ToDictionary(s => s.CandidateId, StringComparer.Ordinal);
        var results = _store.GetResults(jobId);

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[]
        {
            "AttemptedAtUtc", "JobId", "BatchId", "CompanyName", "CompanyFile", "CompanyFingerprint",
            "TxnType", "TxnID", "TxnLineID", "TxnDate", "RefNumber", "Payee", "PostingAccount",
            "LineAmount", "BeforeAccount", "AfterAccount", "IntendedAccount", "ClearedStatus",
            "Outcome", "RequestType", "StatusCode", "StatusMessage", "Detail",
            "VerificationPassed", "ProtectedFieldDifferences", "ElapsedMs",
        }.Select(Escape)));

        foreach (var result in results)
        {
            candidates.TryGetValue(result.CandidateId, out var candidate);
            snapshots.TryGetValue(result.CandidateId, out var snapshot);

            sb.AppendLine(string.Join(',', new[]
            {
                result.AttemptedAt.ToString("O", CultureInfo.InvariantCulture),
                result.JobId,
                result.BatchId,
                job.Company.CompanyName,
                job.Company.CompanyFileName,
                job.Company.Fingerprint,
                candidate?.TxnType.ToString() ?? string.Empty,
                result.TxnId,
                result.TxnLineId,
                candidate?.TxnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                candidate?.RefNumber ?? string.Empty,
                candidate?.PayeeName ?? string.Empty,
                candidate?.PostingAccountName ?? string.Empty,
                candidate?.LineAmount.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                snapshot?.BeforeAccount ?? candidate?.CurrentAccount.ToString() ?? string.Empty,
                snapshot?.AfterAccount ?? string.Empty,
                job.DestinationAccount.FullName,
                candidate?.Cleared.ToString() ?? string.Empty,
                result.Outcome.ToString(),
                result.RequestType ?? string.Empty,
                result.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                result.StatusMessage ?? string.Empty,
                result.Detail ?? string.Empty,
                result.Verification?.Passed.ToString() ?? string.Empty,
                snapshot?.ProtectedFieldComparison ?? string.Empty,
                result.ElapsedMs.ToString(CultureInfo.InvariantCulture),
            }.Select(Escape)));
        }

        WriteAtomically(path, sb.ToString());
    }

    /// <summary>
    /// Writes the complete job record as JSON: rule, candidates, batches, results and the
    /// before/after transaction snapshots.
    /// </summary>
    public void ExportJson(string jobId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var job = _store.GetJob(jobId)
            ?? throw new InvalidOperationException($"Unknown job '{jobId}'.");

        var document = new
        {
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Tool = "QuickBooks Desktop Bulk Reclassification Utility",
            Job = job,
            Batches = _store.GetBatches(jobId),
            Candidates = _store.GetCandidates(jobId),
            Results = _store.GetResults(jobId),
            Snapshots = _store.GetSnapshots(jobId),
        };

        WriteAtomically(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>
    /// Writes a plain-text summary suitable for pasting into a client file note.
    /// </summary>
    public string BuildSummary(string jobId)
    {
        var job = _store.GetJob(jobId)
            ?? throw new InvalidOperationException($"Unknown job '{jobId}'.");

        var results = _store.GetResults(jobId);
        var candidates = _store.GetCandidates(jobId);

        var sb = new StringBuilder();
        sb.AppendLine("QuickBooks Desktop Bulk Reclassification - job summary");
        sb.AppendLine($"Job:          {job.JobId}");
        sb.AppendLine($"Company:      {job.Company.CompanyName}");
        sb.AppendLine($"Company file: {job.Company.CompanyFileName}");
        sb.AppendLine($"Rule:         {job.RuleDescription}");
        sb.AppendLine($"Backup confirmed: {job.BackupConfirmedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "(not confirmed)"}");
        sb.AppendLine();
        sb.AppendLine($"Candidates matched:   {candidates.Count}");
        sb.AppendLine($"  reclassified:       {candidates.Count(c => c.Status == CandidateStatus.Verified)}");
        sb.AppendLine($"  skipped:            {candidates.Count(c => c.Status == CandidateStatus.Skipped)}");
        sb.AppendLine($"  failed:             {candidates.Count(c => c.Status == CandidateStatus.Failed)}");
        sb.AppendLine($"  unsupported:        {candidates.Count(c => !c.IsSelectable)}");
        sb.AppendLine($"  never attempted:    {candidates.Count(c => c.Status is CandidateStatus.Matched or CandidateStatus.Selected)}");
        sb.AppendLine();
        sb.AppendLine($"Write attempts logged: {results.Count}");

        var mismatches = results.Where(r => r.Outcome == ExecutionOutcome.VerificationMismatch).ToList();
        if (mismatches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*** {mismatches.Count} VERIFICATION MISMATCH(ES) - review in QuickBooks ***");
            foreach (var mismatch in mismatches)
            {
                sb.AppendLine($"  TxnID {mismatch.TxnId} line {mismatch.TxnLineId}: {mismatch.Describe()}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes through a temporary file and moves it into place, so an interrupted export cannot
    /// leave a half-written audit file that looks complete.
    /// </summary>
    private static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(temp, path, overwrite: true);
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;

        // Guard against a memo or account name beginning with a formula character being
        // interpreted as one when the export is opened in a spreadsheet.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
