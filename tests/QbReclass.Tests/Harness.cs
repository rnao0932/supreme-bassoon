using QbReclass.Core.Adapters;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;
using QbReclass.Core.Services;
using QbReclass.Simulator;

namespace QbReclass.Tests;

/// <summary>
/// Wires a simulated QuickBooks to the real service stack, so tests exercise the shipping code
/// paths rather than doubles of them. Only <see cref="QbReclass.Core.Session.IQbSession"/> is faked.
/// </summary>
public sealed class Harness : IDisposable
{
    private readonly string _dbPath;

    public Harness(SimulatedCompany company, bool readOnly = false, AdapterRegistry? registry = null)
    {
        Company = company;
        Session = new SimulatedQbSession(company, readOnly);
        Registry = registry ?? AdapterRegistry.Default;

        _dbPath = Path.Combine(Path.GetTempPath(), $"qbreclass-test-{Guid.NewGuid():N}.db");
        Store = new SqliteAuditStore(_dbPath);

        Query = new QueryService(Session, Registry);
        Planner = new ReclassificationPlanner(Registry);
        Preflight = new PreflightService(Query, Registry);
        Verification = new VerificationService(Query);
        Batches = new BatchPlanner();
        Execution = new ExecutionService(Session, Query, Preflight, Verification, Registry, Store);
        Reconciliation = new ReconciliationService(Query, Store);
        Export = new ExportService(Store);
    }

    public SimulatedCompany Company { get; }
    public SimulatedQbSession Session { get; }
    public AdapterRegistry Registry { get; }
    public SqliteAuditStore Store { get; }
    public QueryService Query { get; }
    public ReclassificationPlanner Planner { get; }
    public PreflightService Preflight { get; }
    public VerificationService Verification { get; }
    public BatchPlanner Batches { get; }
    public ExecutionService Execution { get; }
    public ReconciliationService Reconciliation { get; }
    public ExportService Export { get; }

    /// <summary>Builds a job bound to the simulated company, with the backup gate already satisfied.</summary>
    public Job NewJob(
        QbAccount source,
        QbAccount destination,
        DateOnly? from = null,
        DateOnly? to = null,
        IReadOnlyList<TransactionType>? types = null,
        JobFilters? filters = null,
        int batchSize = 25,
        StopPolicy stopPolicy = StopPolicy.StopOnFirstFailure,
        bool confirmBackup = true) => new()
        {
            JobId = $"JOB-{Guid.NewGuid():N}"[..12],
            Company = Company.Identity,
            FromDate = from ?? new DateOnly(2024, 1, 1),
            ToDate = to ?? new DateOnly(2024, 12, 31),
            SourceAccount = source,
            DestinationAccount = destination,
            TransactionTypes = types ?? [TransactionType.CreditCardCharge],
            Filters = filters ?? new JobFilters(),
            BatchSize = batchSize,
            StopPolicy = stopPolicy,
            BackupConfirmedAt = confirmBackup ? DateTimeOffset.UtcNow : null,
        };

    /// <summary>Runs the full read-only preview stage and persists the result.</summary>
    public PlanResult Preview(Job job)
    {
        var transactions = job.TransactionTypes
            .SelectMany(t => Query.QueryTransactions(t, job.FromDate, job.ToDate, job.Filters))
            .ToList();

        var plan = Planner.Plan(job, transactions);

        Store.SaveJob(job);
        Store.SaveCandidates(job.JobId, plan.Candidates);
        return plan;
    }

    /// <summary>Selects the given candidates, builds one batch and approves it.</summary>
    public Batch ApproveBatch(Job job, IEnumerable<CandidateLine> candidates)
    {
        var selected = candidates.ToList();
        Store.SetSelection(selected.Select(c => c.CandidateId), true);

        var refreshed = Store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batch = Batches.SplitIntoBatches(job, refreshed).First();

        batch = Batches.Approve(batch, "test");
        Store.SaveBatch(batch);
        return batch;
    }

    /// <summary>Preview, select everything writable, approve, execute. The common path.</summary>
    public (Job Job, PlanResult Plan, BatchOutcome Outcome) RunToCompletion(Job job)
    {
        var plan = Preview(job);
        var batch = ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));
        var outcome = Execution.Execute(job, batch);
        return (job, plan, outcome);
    }

    public void Dispose()
    {
        Store.Dispose();
        Session.Dispose();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}

/// <summary>Prebuilt company shapes used across the test suite.</summary>
public static class TestCompanies
{
    /// <summary>
    /// A company with a credit card, two expense accounts and a spread of charges: single-line,
    /// multi-line, reconciled, with class/memo/ref data, plus a check.
    /// </summary>
    public static SimulatedCompany Standard(
        out QbAccount source,
        out QbAccount destination,
        out QbAccount creditCard)
    {
        var company = new SimulatedCompany();

        creditCard = company.AddAccount("Business Visa", "CreditCard");
        source = company.AddAccount("Office Expense", "Expense");
        destination = company.AddAccount("Automobile:Fuel", "Expense");
        var other = company.AddAccount("Meals and Entertainment", "Expense");

        // Single-line charge.
        company.AddTransaction(
            TransactionType.CreditCardCharge,
            creditCard,
            new DateOnly(2024, 3, 4),
            "Shell",
            [(source, 62.40m, "fuel stop")],
            refNumber: "1001",
            memo: "March fuel");

        // Multi-line charge where only one line matches the rule.
        company.AddTransaction(
            TransactionType.CreditCardCharge,
            creditCard,
            new DateOnly(2024, 4, 12),
            "Costco",
            [
                (source, 118.00m, "printer paper"),
                (other, 44.25m, "team lunch"),
            ],
            refNumber: "1002");

        // Reconciled charge.
        company.AddTransaction(
            TransactionType.CreditCardCharge,
            creditCard,
            new DateOnly(2024, 5, 20),
            "Chevron",
            [(source, 71.15m, "fuel")],
            refNumber: "1003",
            cleared: ClearedStatus.Reconciled,
            className: "Field Ops");

        // Charge with two lines that both match the rule.
        company.AddTransaction(
            TransactionType.CreditCardCharge,
            creditCard,
            new DateOnly(2024, 6, 1),
            "Amazon",
            [
                (source, 22.00m, "cables"),
                (source, 33.00m, "adapters"),
            ],
            refNumber: "1004");

        // A check on the same source account: preview-only.
        var checking = company.AddAccount("Operating Checking", "Bank");
        company.AddTransaction(
            TransactionType.Check,
            checking,
            new DateOnly(2024, 7, 9),
            "City Utilities",
            [(source, 240.00m, "July power")],
            refNumber: "2051");

        return company;
    }
}
