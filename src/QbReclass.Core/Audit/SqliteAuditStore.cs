using System.Text.Json;
using Microsoft.Data.Sqlite;
using QbReclass.Core.Model;

namespace QbReclass.Core.Audit;

/// <summary>
/// SQLite-backed <see cref="IAuditStore"/> (spec section 12: "local SQLite database or similarly
/// simple durable store").
/// </summary>
/// <remarks>
/// <para>
/// Records are stored as JSON payloads alongside the few columns that are queried or indexed. That
/// keeps the schema stable as the model grows and keeps the audit export a faithful copy of what
/// the application actually saw.
/// </para>
/// <para>
/// The connection runs with <c>journal_mode=WAL</c> and <c>synchronous=FULL</c>. FULL is slower
/// than the usual NORMAL, and that is the point: <see cref="MarkSubmitted"/> must survive an
/// abrupt power loss between the local commit and the QuickBooks write, or restart reconciliation
/// has nothing to work from.
/// </para>
/// </remarks>
public sealed class SqliteAuditStore : IAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly SqliteConnection _connection;

    public SqliteAuditStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    /// <summary>Opens a store in the per-user application data directory (spec section 17).</summary>
    public static SqliteAuditStore OpenDefault(string? appDataRoot = null)
    {
        var root = appDataRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QbReclass");

        return new SqliteAuditStore(Path.Combine(root, "qbreclass.db"));
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS jobs (
                job_id      TEXT PRIMARY KEY,
                company_fp  TEXT NOT NULL,
                status      TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                payload     TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS candidates (
                candidate_id TEXT PRIMARY KEY,
                job_id       TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                txn_id       TEXT NOT NULL,
                txn_line_id  TEXT NOT NULL,
                status       TEXT NOT NULL,
                is_selected  INTEGER NOT NULL DEFAULT 0,
                batch_id     TEXT NULL,
                payload      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_candidates_job ON candidates(job_id, status);
            CREATE INDEX IF NOT EXISTS ix_candidates_batch ON candidates(batch_id);

            CREATE TABLE IF NOT EXISTS batches (
                batch_id  TEXT PRIMARY KEY,
                job_id    TEXT NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                sequence  INTEGER NOT NULL,
                status    TEXT NOT NULL,
                payload   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_batches_job ON batches(job_id);

            CREATE TABLE IF NOT EXISTS results (
                result_id    INTEGER PRIMARY KEY AUTOINCREMENT,
                candidate_id TEXT NOT NULL,
                batch_id     TEXT NOT NULL,
                job_id       TEXT NOT NULL,
                attempted_at TEXT NOT NULL,
                outcome      TEXT NOT NULL,
                payload      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_results_job ON results(job_id);
            CREATE INDEX IF NOT EXISTS ix_results_batch ON results(batch_id);
            CREATE INDEX IF NOT EXISTS ix_results_candidate ON results(candidate_id);

            CREATE TABLE IF NOT EXISTS snapshots (
                candidate_id TEXT PRIMARY KEY,
                job_id       TEXT NOT NULL,
                txn_id       TEXT NOT NULL,
                payload      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_snapshots_job ON snapshots(job_id);
            """);
    }

    public void SaveJob(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jobs (job_id, company_fp, status, created_at, payload)
            VALUES ($id, $fp, $status, $created, $payload)
            ON CONFLICT(job_id) DO UPDATE SET
                status = excluded.status,
                payload = excluded.payload;
            """;
        cmd.Parameters.AddWithValue("$id", job.JobId);
        cmd.Parameters.AddWithValue("$fp", job.Company.Fingerprint);
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job, JsonOptions));
        cmd.ExecuteNonQuery();
    }

    public void UpdateJob(Job job) => SaveJob(job);

    public Job? GetJob(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM jobs WHERE job_id = $id;";
        cmd.Parameters.AddWithValue("$id", jobId);

        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : JsonSerializer.Deserialize<Job>(payload, JsonOptions);
    }

    public IReadOnlyList<Job> ListJobs()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM jobs ORDER BY created_at DESC;";
        return ReadAll<Job>(cmd);
    }

    public void SaveCandidates(string jobId, IEnumerable<CandidateLine> candidates)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(candidates);

        using var tx = _connection.BeginTransaction();

        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM candidates WHERE job_id = $job;";
            clear.Parameters.AddWithValue("$job", jobId);
            clear.ExecuteNonQuery();
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO candidates
                (candidate_id, job_id, txn_id, txn_line_id, status, is_selected, batch_id, payload)
            VALUES ($id, $job, $txn, $line, $status, $selected, $batch, $payload);
            """;

        var pId = insert.Parameters.Add("$id", SqliteType.Text);
        var pJob = insert.Parameters.Add("$job", SqliteType.Text);
        var pTxn = insert.Parameters.Add("$txn", SqliteType.Text);
        var pLine = insert.Parameters.Add("$line", SqliteType.Text);
        var pStatus = insert.Parameters.Add("$status", SqliteType.Text);
        var pSelected = insert.Parameters.Add("$selected", SqliteType.Integer);
        var pBatch = insert.Parameters.Add("$batch", SqliteType.Text);
        var pPayload = insert.Parameters.Add("$payload", SqliteType.Text);

        foreach (var candidate in candidates)
        {
            pId.Value = candidate.CandidateId;
            pJob.Value = jobId;
            pTxn.Value = candidate.TxnId;
            pLine.Value = candidate.TxnLineId;
            pStatus.Value = candidate.Status.ToString();
            pSelected.Value = candidate.IsSelected ? 1 : 0;
            pBatch.Value = (object?)candidate.BatchId ?? DBNull.Value;
            pPayload.Value = JsonSerializer.Serialize(candidate, JsonOptions);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<CandidateLine> GetCandidates(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM candidates WHERE job_id = $job ORDER BY rowid;";
        cmd.Parameters.AddWithValue("$job", jobId);
        return ReadAll<CandidateLine>(cmd);
    }

    public IReadOnlyList<CandidateLine> GetCandidatesByStatus(string jobId, CandidateStatus status)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT payload FROM candidates WHERE job_id = $job AND status = $status ORDER BY rowid;";
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.Parameters.AddWithValue("$status", status.ToString());
        return ReadAll<CandidateLine>(cmd);
    }

    public CandidateLine? GetCandidate(string candidateId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM candidates WHERE candidate_id = $id;";
        cmd.Parameters.AddWithValue("$id", candidateId);

        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : JsonSerializer.Deserialize<CandidateLine>(payload, JsonOptions);
    }

    public void UpdateCandidate(CandidateLine candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE candidates
               SET status = $status,
                   is_selected = $selected,
                   batch_id = $batch,
                   payload = $payload
             WHERE candidate_id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", candidate.Status.ToString());
        cmd.Parameters.AddWithValue("$selected", candidate.IsSelected ? 1 : 0);
        cmd.Parameters.AddWithValue("$batch", (object?)candidate.BatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(candidate, JsonOptions));
        cmd.Parameters.AddWithValue("$id", candidate.CandidateId);
        cmd.ExecuteNonQuery();
    }

    public void SetSelection(IEnumerable<string> candidateIds, bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);

        // Read before opening the transaction: Microsoft.Data.Sqlite rejects a command whose
        // Transaction does not match the connection's active one.
        var targets = candidateIds
            .Select(GetCandidate)
            .OfType<CandidateLine>()
            .ToList();

        using var tx = _connection.BeginTransaction();

        foreach (var candidate in targets)
        {
            // A candidate that has already been written, skipped or failed is finished. Leaving it
            // alone here is what stops a blanket "select none" from resetting completed work back to
            // Matched, which would make it eligible for a second write (FR-016).
            if (candidate.IsTerminal)
            {
                continue;
            }

            // Unsupported rows can never be selected, whatever the caller asks for (FR-018).
            var effective = isSelected && candidate.IsSelectable;
            var updated = candidate with
            {
                IsSelected = effective,
                Status = effective ? CandidateStatus.Selected : CandidateStatus.Matched,
            };

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE candidates
                   SET is_selected = $selected, status = $status, payload = $payload
                 WHERE candidate_id = $id;
                """;
            cmd.Parameters.AddWithValue("$selected", effective ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", updated.Status.ToString());
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(updated, JsonOptions));
            cmd.Parameters.AddWithValue("$id", candidate.CandidateId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void SaveBatch(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var tx = _connection.BeginTransaction();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO batches (batch_id, job_id, sequence, status, payload)
                VALUES ($id, $job, $seq, $status, $payload)
                ON CONFLICT(batch_id) DO UPDATE SET
                    status = excluded.status,
                    payload = excluded.payload;
                """;
            cmd.Parameters.AddWithValue("$id", batch.BatchId);
            cmd.Parameters.AddWithValue("$job", batch.JobId);
            cmd.Parameters.AddWithValue("$seq", batch.Sequence);
            cmd.Parameters.AddWithValue("$status", batch.Status.ToString());
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(batch, JsonOptions));
            cmd.ExecuteNonQuery();
        }

        using (var link = _connection.CreateCommand())
        {
            link.Transaction = tx;
            link.CommandText = "UPDATE candidates SET batch_id = $batch WHERE candidate_id = $id;";
            var pBatch = link.Parameters.Add("$batch", SqliteType.Text);
            var pId = link.Parameters.Add("$id", SqliteType.Text);

            foreach (var candidateId in batch.CandidateIds)
            {
                pBatch.Value = batch.BatchId;
                pId.Value = candidateId;
                link.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public Batch? GetBatch(string batchId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM batches WHERE batch_id = $id;";
        cmd.Parameters.AddWithValue("$id", batchId);

        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : JsonSerializer.Deserialize<Batch>(payload, JsonOptions);
    }

    public IReadOnlyList<Batch> GetBatches(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM batches WHERE job_id = $job ORDER BY sequence;";
        cmd.Parameters.AddWithValue("$job", jobId);
        return ReadAll<Batch>(cmd);
    }

    public void MarkSubmitted(string candidateId, string batchId, string requestType)
    {
        var candidate = GetCandidate(candidateId)
            ?? throw new InvalidOperationException($"Unknown candidate '{candidateId}'.");

        // Written and committed before the request is sent. If the process dies here, restart
        // reconciliation finds a Submitted candidate and re-queries QuickBooks to learn the truth.
        // Deliberately no result row yet: the results table is the append-only log of completed
        // attempts, one row per attempt. The Submitted status is what marks an attempt in flight,
        // so an interrupted write cannot inflate the success/failure counts on the results screen.
        UpdateCandidate(candidate with
        {
            Status = CandidateStatus.Submitted,
            BatchId = batchId,
            SubmittedRequestType = requestType,
        });
    }

    public void SaveResult(ExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO results (candidate_id, batch_id, job_id, attempted_at, outcome, payload)
            VALUES ($candidate, $batch, $job, $at, $outcome, $payload);
            """;
        cmd.Parameters.AddWithValue("$candidate", result.CandidateId);
        cmd.Parameters.AddWithValue("$batch", result.BatchId);
        cmd.Parameters.AddWithValue("$job", result.JobId);
        cmd.Parameters.AddWithValue("$at", result.AttemptedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, JsonOptions));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ExecutionResult> GetResults(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM results WHERE job_id = $job ORDER BY result_id;";
        cmd.Parameters.AddWithValue("$job", jobId);
        return ReadAll<ExecutionResult>(cmd);
    }

    public IReadOnlyList<ExecutionResult> GetResultsForBatch(string batchId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM results WHERE batch_id = $batch ORDER BY result_id;";
        cmd.Parameters.AddWithValue("$batch", batchId);
        return ReadAll<ExecutionResult>(cmd);
    }

    public void SaveSnapshot(AuditSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var candidate = GetCandidate(snapshot.CandidateId);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots (candidate_id, job_id, txn_id, payload)
            VALUES ($candidate, $job, $txn, $payload)
            ON CONFLICT(candidate_id) DO UPDATE SET payload = excluded.payload;
            """;
        cmd.Parameters.AddWithValue("$candidate", snapshot.CandidateId);
        cmd.Parameters.AddWithValue("$job", candidate?.JobId ?? string.Empty);
        cmd.Parameters.AddWithValue("$txn", snapshot.TxnId);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, JsonOptions));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditSnapshot> GetSnapshots(string jobId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM snapshots WHERE job_id = $job;";
        cmd.Parameters.AddWithValue("$job", jobId);
        return ReadAll<AuditSnapshot>(cmd);
    }

    public AuditSnapshot? GetSnapshot(string candidateId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT payload FROM snapshots WHERE candidate_id = $id;";
        cmd.Parameters.AddWithValue("$id", candidateId);

        var payload = cmd.ExecuteScalar() as string;
        return payload is null ? null : JsonSerializer.Deserialize<AuditSnapshot>(payload, JsonOptions);
    }

    private static List<T> ReadAll<T>(SqliteCommand cmd)
    {
        var items = new List<T>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
