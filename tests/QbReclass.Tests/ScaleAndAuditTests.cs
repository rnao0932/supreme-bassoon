using System.Diagnostics;
using System.Text.Json;
using QbReclass.Core.Model;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>Large-population behaviour (spec section 16, section 18) and audit export (FR-017).</summary>
public sealed class ScaleAndAuditTests
{
    /// <summary>
    /// Spec section 18: a large preview population, roughly 10,000 candidate lines. The assertion
    /// is about correctness and paging, not a wall-clock target - the spec is explicit that
    /// correctness outranks throughput.
    /// </summary>
    [Fact]
    public void APreviewOfTenThousandCandidateLinesIsPagedAndAccurate()
    {
        const int TransactionCount = 5_000;

        var company = new SimulatedCompany { PageSizeCap = 200 };
        var card = company.AddAccount("Business Visa", "CreditCard");
        var source = company.AddAccount("Office Expense", "Expense");
        var destination = company.AddAccount("Automobile:Fuel", "Expense");
        var other = company.AddAccount("Meals", "Expense");

        for (var i = 0; i < TransactionCount; i++)
        {
            company.AddTransaction(
                TransactionType.CreditCardCharge,
                card,
                new DateOnly(2024, 1, 1).AddDays(i % 365),
                $"Vendor {i % 50}",
                [
                    (source, 10m, "matching a"),
                    (source, 20m, "matching b"),
                    (other, 30m, "not matching"),
                ],
                refNumber: $"R{i:D5}");
        }

        using var harness = new Harness(company);
        var job = harness.NewJob(source, destination);

        var stopwatch = Stopwatch.StartNew();
        var plan = harness.Preview(job);
        stopwatch.Stop();

        Assert.Equal(TransactionCount * 2, plan.Candidates.Count);
        Assert.Equal(TransactionCount, plan.TransactionsExamined);
        Assert.Equal(TransactionCount * 30m, plan.TotalAmount);
        Assert.All(plan.Candidates, c => Assert.Equal(source.ListId, c.CurrentAccount.ListId));

        // Paging really happened rather than one giant response.
        var queryRequests = harness.Session.SentRequests
            .Count(r => r.Contains("CreditCardChargeQueryRq", StringComparison.Ordinal));
        Assert.True(queryRequests >= TransactionCount / company.PageSizeCap, $"only {queryRequests} query pages");

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMinutes(2),
            $"preview of {plan.Candidates.Count} candidates took {stopwatch.Elapsed}");
    }

    /// <summary>The preview stage issues no modification request of any kind (spec FR-002, section 19).</summary>
    [Fact]
    public void ThePreviewStageWritesNothing()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var before = company.Transactions
            .Select(t => (t.TxnId, t.EditSequence, Lines: t.Lines.Select(l => l.Account.ListId).ToList()))
            .ToList();

        harness.Preview(harness.NewJob(
            source, destination, types: [TransactionType.CreditCardCharge, TransactionType.Check]));

        Assert.All(harness.Session.SentRequests, r =>
            Assert.DoesNotContain("Mod", r.Split('<').FirstOrDefault(s => s.EndsWith("Rq", StringComparison.Ordinal)) ?? string.Empty, StringComparison.Ordinal));

        foreach (var (txnId, editSequence, accounts) in before)
        {
            var after = company.Find(txnId)!;
            Assert.Equal(editSequence, after.EditSequence);
            Assert.Equal(accounts, after.Lines.Select(l => l.Account.ListId).ToList());
        }
    }

    /// <summary>FR-017: the CSV audit export carries before/after classification and QuickBooks identifiers.</summary>
    [Fact]
    public void TheCsvExportCarriesBeforeAndAfterClassificationWithIdentifiers()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        var (_, _, outcome) = harness.RunToCompletion(job);
        Assert.True(outcome.SucceededCount > 0);

        var path = Path.Combine(Path.GetTempPath(), $"qbreclass-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Export.ExportCsv(job.JobId, path);

            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length > 1);

            var header = lines[0];
            foreach (var column in new[]
                     {
                         "TxnID", "TxnLineID", "BeforeAccount", "AfterAccount", "IntendedAccount",
                         "Outcome", "StatusCode", "StatusMessage", "CompanyFingerprint",
                         "VerificationPassed", "ProtectedFieldDifferences",
                     })
            {
                Assert.Contains($"\"{column}\"", header, StringComparison.Ordinal);
            }

            var body = string.Join('\n', lines.Skip(1));
            Assert.Contains(source.FullName, body, StringComparison.Ordinal);
            Assert.Contains(destination.FullName, body, StringComparison.Ordinal);
            Assert.Contains(job.Company.CompanyFileName, body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A memo starting with '=' must not become a spreadsheet formula on export.</summary>
    [Fact]
    public void TheCsvExportNeutralizesFormulaLikeText()
    {
        var company = new SimulatedCompany();
        var card = company.AddAccount("Visa", "CreditCard");
        var source = company.AddAccount("Office", "Expense");
        var destination = company.AddAccount("Fuel", "Expense");

        company.AddTransaction(
            TransactionType.CreditCardCharge,
            card,
            new DateOnly(2024, 1, 5),
            "=cmd|'/c calc'!A1",
            [(source, 10m, "=1+1")],
            refNumber: "=HYPERLINK(\"x\")");

        using var harness = new Harness(company);
        var job = harness.NewJob(source, destination);
        harness.RunToCompletion(job);

        var path = Path.Combine(Path.GetTempPath(), $"qbreclass-{Guid.NewGuid():N}.csv");
        try
        {
            harness.Export.ExportCsv(job.JobId, path);
            var text = File.ReadAllText(path);

            Assert.DoesNotContain("\"=", text, StringComparison.Ordinal);
            Assert.Contains("\"'=cmd", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>FR-017: the JSON export is a complete, re-readable record of the job.</summary>
    [Fact]
    public void TheJsonExportRoundTripsTheWholeJob()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        harness.RunToCompletion(job);

        var path = Path.Combine(Path.GetTempPath(), $"qbreclass-{Guid.NewGuid():N}.json");
        try
        {
            harness.Export.ExportJson(job.JobId, path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            Assert.Equal(job.JobId, root.GetProperty("Job").GetProperty("JobId").GetString());
            Assert.True(root.GetProperty("Candidates").GetArrayLength() > 0);
            Assert.True(root.GetProperty("Results").GetArrayLength() > 0);
            Assert.True(root.GetProperty("Batches").GetArrayLength() > 0);
            Assert.True(root.GetProperty("Snapshots").GetArrayLength() > 0);

            var snapshot = root.GetProperty("Snapshots")[0];
            Assert.False(string.IsNullOrEmpty(snapshot.GetProperty("BeforeJson").GetString()));
            Assert.False(string.IsNullOrEmpty(snapshot.GetProperty("AfterJson").GetString()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Every write attempt produces a result record, including skips (spec section 10, "Logging").</summary>
    [Fact]
    public void EveryAttemptIsLoggedIncludingSkips()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, batchSize: 100);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));

        // Make one transaction stale so the batch contains a skip alongside successes.
        var stale = company.Transactions.First(t => t.TxnType == TransactionType.CreditCardCharge);
        company.EditExternally(stale.TxnId, t => t with { Memo = "touched" });

        var outcome = harness.Execution.Execute(job, batch);
        var logged = harness.Store.GetResultsForBatch(batch.BatchId);

        Assert.Equal(outcome.Results.Count, logged.Count);
        Assert.Contains(logged, r => r.Outcome == ExecutionOutcome.Skipped);
        Assert.All(logged, r => Assert.False(string.IsNullOrWhiteSpace(r.Describe())));

        var summary = harness.Export.BuildSummary(job.JobId);
        Assert.Contains(job.Company.CompanyName, summary, StringComparison.Ordinal);
        Assert.Contains("Candidates matched:", summary, StringComparison.Ordinal);
    }

    /// <summary>The audit store survives being closed and reopened, as it must across restarts.</summary>
    [Fact]
    public void TheAuditStoreSurvivesAReopen()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        var dbPath = Path.Combine(Path.GetTempPath(), $"qbreclass-reopen-{Guid.NewGuid():N}.db");

        string jobId;
        int candidateCount;

        try
        {
            using (var harness = new Harness(company))
            {
                var job = harness.NewJob(source, destination);
                var plan = harness.Preview(job);
                jobId = job.JobId;
                candidateCount = plan.Candidates.Count;

                // Copy the harness's store contents into a standalone file by replaying the writes.
                using var persistent = new Core.Audit.SqliteAuditStore(dbPath);
                persistent.SaveJob(job);
                persistent.SaveCandidates(job.JobId, plan.Candidates);
            }

            using var reopened = new Core.Audit.SqliteAuditStore(dbPath);

            var restored = reopened.GetJob(jobId);
            Assert.NotNull(restored);
            Assert.Equal(source.ListId, restored!.SourceAccount.ListId);
            Assert.Equal(destination.ListId, restored.DestinationAccount.ListId);
            Assert.Equal(candidateCount, reopened.GetCandidates(jobId).Count);
            Assert.NotNull(restored.BackupConfirmedAt);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
