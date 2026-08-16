using System.Globalization;
using QbReclass.Core.Adapters;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Services;
using QbReclass.Core.Session;
using QbReclass.Simulator;

namespace QbReclass.Cli;

/// <summary>
/// Console front end.
/// </summary>
/// <remarks>
/// <para>
/// The spec asks for a spike before a user interface (section 25). This is that: it drives the same
/// services the desktop application does, so anything proven here is proven for the product, and it
/// can be run on a client workstation without installing the full application.
/// </para>
/// <para>
/// It also runs against the built-in simulator with <c>--simulate</c>, which is how the workflow can
/// be demonstrated and regression-tested on a machine with no QuickBooks.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// The QuickBooks request processor is an apartment-threaded COM component, so the entry point
    /// must be STA. The attribute is inert on non-Windows hosts.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var cli = CommandLine.Parse(args);

        try
        {
            return cli.Verb switch
            {
                "spike" => Spike(cli),
                "accounts" => Accounts(cli),
                "preview" => Preview(cli),
                "run" => Run(cli),
                "reconcile" => Reconcile(cli),
                "export" => Export(cli),
                "jobs" => Jobs(cli),
                "demo" => Demo(),
                _ => Help(),
            };
        }
        catch (QbCompanyMismatchException ex)
        {
            return Fail(ex.Message);
        }
        catch (QbSessionException ex)
        {
            return Fail(ex.Message);
        }
        catch (QbQueryException ex)
        {
            return Fail(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            QuickBooks Desktop Bulk Reclassification Utility

            USAGE
              qbreclass <command> [options]

            COMMANDS
              spike       Phase 0 feasibility probe. Reports what this QuickBooks actually supports.
              accounts    List the chart of accounts with ListIDs.
              preview     Read-only preview of a reclassification rule. Writes nothing.
              run         Preview, approve a batch, execute, verify. The full workflow.
              reconcile   Resolve writes interrupted by a crash or a lost connection.
              export      Write the audit log for a job as CSV and/or JSON.
              jobs        List jobs held in the local store.
              demo        Run the whole workflow against a built-in simulated company.

            CONNECTION
              --simulate            Use the built-in simulated company instead of QuickBooks.
              --company <path>      .QBW file to open. Omit to use whatever QuickBooks has open.
              --launch              Allow QuickBooks to be started if it is not running.
              --allow-write         Open the session in write mode. Read-only otherwise.
              --db <path>           Local audit database. Defaults to the per-user app data folder.

            RULE (preview, run)
              --from <yyyy-mm-dd>   Start date.                            [required]
              --to <yyyy-mm-dd>     End date.                              [required]
              --source <ListID>     Account the lines currently post to.   [required]
              --dest <ListID>       Account they should post to.           [required]
              --types <list>        Comma-separated. Default CreditCardCharge.
              --payee <text>        Payee contains.
              --memo <text>         Memo contains.
              --ref <text>          Reference/check number contains.
              --min <amount>        Minimum absolute line amount.
              --max <amount>        Maximum absolute line amount.
              --class <name>        Class full name.

            EXECUTION (run)
              --batch-size <n>      Lines per batch. Default 25.
              --backup-confirmed    Assert that a current QuickBooks backup exists. Required to write.
              --yes                 Approve every batch without prompting. Use only in a test company.
              --continue-on-error   Continue past isolated failures. A verification mismatch still stops.

            SPIKE
              --write-txn <TxnID>   Credit card charge to exercise the write path against.
              --write-dest <ListID> Account to move a line to and back again.
              --confirm-test-company
                                    Acknowledge the open file is disposable. Required for a write probe.
              --report <path>       Write the spike report to this file as well as the console.

            EXPORT
              --job <JobId>         Job to export.                         [required]
              --csv <path>          CSV destination.
              --json <path>         JSON destination.
            """);

        return 0;
    }

    // ---------------------------------------------------------------- commands

    private static int Spike(CommandLine cli)
    {
        using var context = Context.Open(cli);

        var probe = new FeasibilityProbe(context.Session, context.Query, context.Registry, Console.Error.WriteLine);

        var report = probe.Run(new ProbeOptions
        {
            WriteProbeTxnId = cli.Get("write-txn"),
            WriteProbeDestinationListId = cli.Get("write-dest"),
            ConfirmedDisposableTestCompany = cli.Has("confirm-test-company"),
            FromDate = cli.GetDate("from", new DateOnly(DateTime.Today.Year - 2, 1, 1)),
            ToDate = cli.GetDate("to", DateOnly.FromDateTime(DateTime.Today)),
        });

        var text = report.Render();
        Console.WriteLine(text);

        if (cli.Get("report") is { } path)
        {
            File.WriteAllText(path, text);
            Console.WriteLine($"Report written to {Path.GetFullPath(path)}");
        }

        return report.HasBlockers ? 2 : 0;
    }

    private static int Accounts(CommandLine cli)
    {
        using var context = Context.Open(cli);

        Console.WriteLine($"Company: {context.Session.Info.Company}");
        Console.WriteLine($"qbXML:   {context.Session.Info.QbXmlVersion}");
        Console.WriteLine();
        Console.WriteLine($"{"ListID",-22} {"Type",-18} {"Active",-7} Full name");

        foreach (var account in context.Query.LoadAccounts().OrderBy(a => a.FullName, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"{account.ListId,-22} {account.AccountType,-18} {(account.IsActive ? "yes" : "NO"),-7} {account.FullName}");
        }

        return 0;
    }

    private static int Preview(CommandLine cli)
    {
        using var context = Context.Open(cli);

        var job = BuildJob(cli, context);
        var errors = job.Validate();

        if (errors.Count > 0)
        {
            return Fail(string.Join(Environment.NewLine, errors));
        }

        var plan = RunPreview(context, job);
        PrintPreview(job, plan);

        Console.WriteLine();
        Console.WriteLine($"Job {job.JobId} saved. Nothing has been changed in QuickBooks.");
        return 0;
    }

    private static int Run(CommandLine cli)
    {
        using var context = Context.Open(cli, requireWrite: true);

        var job = BuildJob(cli, context);
        var errors = job.Validate();

        if (errors.Count > 0)
        {
            return Fail(string.Join(Environment.NewLine, errors));
        }

        if (job.BackupConfirmedAt is null)
        {
            return Fail(
                "Write mode requires --backup-confirmed. Take a QuickBooks backup first; this utility "
                + "does not create one for you.");
        }

        var plan = RunPreview(context, job);
        PrintPreview(job, plan);

        var writable = context.Store.GetCandidates(job.JobId).Where(c => c.IsSelectable).ToList();
        if (writable.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("Nothing in this population can be written. Stopping.");
            return 0;
        }

        context.Store.SetSelection(writable.Select(c => c.CandidateId), true);
        var selected = context.Store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batches = context.Batches.SplitIntoBatches(job, selected);

        Console.WriteLine();
        Console.WriteLine($"{selected.Count} line(s) in {batches.Count} batch(es) of up to {job.BatchSize}.");

        foreach (var pending in batches)
        {
            var summary = context.Batches.Summarize(job, pending, selected);
            PrintApproval(pending, summary);

            if (!Confirm(cli, summary))
            {
                Console.WriteLine("Stopped. No further batches will run.");
                return 0;
            }

            var approved = context.Batches.Approve(pending, Environment.UserName);
            context.Store.SaveBatch(approved);

            var progress = new Progress<ExecutionProgress>(p =>
                Console.WriteLine($"  [{p.Completed}/{p.Total}] {p.CurrentTxnId}: {p.LastResult?.Describe()}"));

            var outcome = context.Execution.Execute(job, approved, progress);

            Console.WriteLine();
            Console.WriteLine($"Batch {approved.Sequence}: {outcome.Summary}");

            if (outcome.Halted)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"HALTED: {outcome.HaltReason}");
                Console.Error.WriteLine(
                    $"{outcome.UnattemptedCandidateIds.Count} line(s) in this batch were not attempted, "
                    + "and no further batch will start.");
                return 3;
            }

            // The next batch never starts on its own (spec section 6 step 12).
            Console.WriteLine();
        }

        Console.WriteLine(context.Export.BuildSummary(job.JobId));
        return 0;
    }

    private static int Reconcile(CommandLine cli)
    {
        using var context = Context.Open(cli);

        var jobId = cli.Require("job");
        var job = context.Store.GetJob(jobId) ?? throw new InvalidOperationException($"Unknown job '{jobId}'.");

        var outcome = context.Reconciliation.Reconcile(job);

        Console.WriteLine(outcome.Summary);
        foreach (var item in outcome.Items)
        {
            Console.WriteLine($"  {item.TxnId}: {item.Verdict} - {item.Detail}");
        }

        return outcome.NeedsAttention ? 2 : 0;
    }

    private static int Export(CommandLine cli)
    {
        using var store = OpenStore(cli);
        var export = new ExportService(store);
        var jobId = cli.Require("job");

        if (cli.Get("csv") is { } csv)
        {
            export.ExportCsv(jobId, csv);
            Console.WriteLine($"CSV written to {Path.GetFullPath(csv)}");
        }

        if (cli.Get("json") is { } json)
        {
            export.ExportJson(jobId, json);
            Console.WriteLine($"JSON written to {Path.GetFullPath(json)}");
        }

        if (!cli.Has("csv") && !cli.Has("json"))
        {
            Console.WriteLine(export.BuildSummary(jobId));
        }

        return 0;
    }

    private static int Jobs(CommandLine cli)
    {
        using var store = OpenStore(cli);

        foreach (var job in store.ListJobs())
        {
            var candidates = store.GetCandidates(job.JobId);
            Console.WriteLine(
                $"{job.JobId}  {job.CreatedAt:yyyy-MM-dd HH:mm}  {job.Status,-10} "
                + $"{candidates.Count,6} lines  {job.RuleDescription}");
        }

        return 0;
    }

    /// <summary>
    /// Runs the whole workflow against a simulated company. Everything it exercises is the real
    /// service stack; only QuickBooks is substituted.
    /// </summary>
    private static int Demo()
    {
        var company = BuildDemoCompany(out var source, out var destination);

        using var session = new SimulatedQbSession(company);
        using var store = new SqliteAuditStore(
            Path.Combine(Path.GetTempPath(), $"qbreclass-demo-{Guid.NewGuid():N}.db"));

        var registry = AdapterRegistry.Default;
        var query = new QueryService(session, registry);
        var planner = new ReclassificationPlanner(registry);
        var preflight = new PreflightService(query, registry);
        var verification = new VerificationService(query);
        var batches = new BatchPlanner();
        var execution = new ExecutionService(session, query, preflight, verification, registry, store);
        var export = new ExportService(store);

        var job = new Job
        {
            JobId = "DEMO",
            Company = company.Identity,
            FromDate = new DateOnly(2024, 1, 1),
            ToDate = new DateOnly(2024, 12, 31),
            SourceAccount = source,
            DestinationAccount = destination,
            TransactionTypes = [TransactionType.CreditCardCharge, TransactionType.Check],
            BatchSize = 3,
            BackupConfirmedAt = DateTimeOffset.UtcNow,
        };

        var transactions = job.TransactionTypes
            .SelectMany(t => query.QueryTransactions(t, job.FromDate, job.ToDate))
            .ToList();

        var plan = planner.Plan(job, transactions);
        store.SaveJob(job);
        store.SaveCandidates(job.JobId, plan.Candidates);

        PrintPreview(job, plan);

        var writable = plan.Candidates.Where(c => c.IsSelectable).ToList();
        store.SetSelection(writable.Select(c => c.CandidateId), true);
        var selected = store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();

        foreach (var pending in batches.SplitIntoBatches(job, selected))
        {
            var summary = batches.Summarize(job, pending, selected);
            PrintApproval(pending, summary);

            var approved = batches.Approve(pending, "demo");
            store.SaveBatch(approved);

            var outcome = execution.Execute(job, approved);
            Console.WriteLine($"  -> {outcome.Summary}");
            Console.WriteLine();
        }

        Console.WriteLine(export.BuildSummary(job.JobId));

        Console.WriteLine("Resulting state in the simulated company:");
        foreach (var txn in company.Transactions.OrderBy(t => t.TxnDate))
        {
            foreach (var line in txn.ExpenseLines)
            {
                Console.WriteLine(
                    $"  {txn.TxnType,-18} {txn.TxnDate:yyyy-MM-dd} {txn.RefNumber,-6} "
                    + $"{line.Amount,10:0.00}  {line.Account}");
            }
        }

        return 0;
    }

    // ---------------------------------------------------------------- helpers

    private static SimulatedCompany BuildDemoCompany(out QbAccount source, out QbAccount destination)
    {
        var company = new SimulatedCompany("Demo Bookkeeping LLC");

        var card = company.AddAccount("Business Visa", "CreditCard");
        var bank = company.AddAccount("Operating Checking", "Bank");
        source = company.AddAccount("Ask My Accountant", "Expense");
        destination = company.AddAccount("Automobile:Fuel", "Expense");
        var meals = company.AddAccount("Meals and Entertainment", "Expense");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(2024, 2, 3), "Shell",
            [(source, 54.10m, "fuel")], refNumber: "C-1001");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(2024, 3, 11), "Costco",
            [(source, 88.00m, "fuel"), (meals, 31.75m, "lunch")], refNumber: "C-1002");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(2024, 4, 18), "Chevron",
            [(source, 63.90m, "fuel")], refNumber: "C-1003", cleared: ClearedStatus.Reconciled);

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(2024, 5, 2), "Amazon",
            [(source, 19.99m, "wiper blades"), (source, 24.50m, "oil")], refNumber: "C-1004");

        company.AddTransaction(
            TransactionType.Check, bank, new DateOnly(2024, 6, 7), "Valley Fuel Co",
            [(source, 412.00m, "bulk diesel")], refNumber: "2210");

        return company;
    }

    private static Job BuildJob(CommandLine cli, Context context)
    {
        var accounts = context.Query.LoadAccounts();

        var source = ResolveAccount(accounts, cli.Require("source"), "source");
        var destination = ResolveAccount(accounts, cli.Require("dest"), "destination");

        var types = (cli.Get("types") ?? nameof(TransactionType.CreditCardCharge))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.TryParse<TransactionType>(t, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"Unknown transaction type '{t}'."))
            .ToList();

        return new Job
        {
            JobId = $"JOB-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
            Company = context.Session.Info.Company,
            FromDate = cli.GetDate("from", DateOnly.MinValue),
            ToDate = cli.GetDate("to", DateOnly.MaxValue),
            SourceAccount = source,
            DestinationAccount = destination,
            TransactionTypes = types,
            BatchSize = cli.GetInt("batch-size", 25),
            StopPolicy = cli.Has("continue-on-error")
                ? StopPolicy.ContinueOnIsolatedFailure
                : StopPolicy.StopOnFirstFailure,
            BackupConfirmedAt = cli.Has("backup-confirmed") ? DateTimeOffset.UtcNow : null,
            Filters = new JobFilters
            {
                PayeeContains = cli.Get("payee"),
                MemoContains = cli.Get("memo"),
                RefNumberContains = cli.Get("ref"),
                MinAmount = cli.GetDecimal("min"),
                MaxAmount = cli.GetDecimal("max"),
                ClassFullName = cli.Get("class"),
            },
        };
    }

    private static QbAccount ResolveAccount(IReadOnlyList<QbAccount> accounts, string token, string role)
    {
        var match = accounts.FirstOrDefault(a => string.Equals(a.ListId, token, StringComparison.Ordinal))
            ?? accounts.FirstOrDefault(a => string.Equals(a.FullName, token, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new ArgumentException(
            $"No {role} account matches '{token}'. Run 'qbreclass accounts' to list ListIDs.");
    }

    private static PlanResult RunPreview(Context context, Job job)
    {
        var transactions = job.TransactionTypes
            .SelectMany(t => context.Query.QueryTransactions(t, job.FromDate, job.ToDate, job.Filters))
            .ToList();

        var plan = context.Planner.Plan(job, transactions);
        context.Store.SaveJob(job);
        context.Store.SaveCandidates(job.JobId, plan.Candidates);
        return plan;
    }

    private static void PrintPreview(Job job, PlanResult plan)
    {
        Console.WriteLine();
        Console.WriteLine($"Company: {job.Company.CompanyName}  ({job.Company.CompanyFileName})");
        Console.WriteLine($"Rule:    {job.RuleDescription}");
        Console.WriteLine();
        Console.WriteLine(
            $"{"Type",-18} {"Date",-10} {"Ref",-8} {"Payee",-20} {"Amount",12}  "
            + $"{"Current",-24} {"Proposed",-24} {"Rec",-10} Status");
        Console.WriteLine(new string('-', 150));

        foreach (var c in plan.Candidates.OrderBy(c => c.TxnDate).ThenBy(c => c.TxnId, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"{c.TxnType,-18} {c.TxnDate:yyyy-MM-dd} {Trim(c.RefNumber, 8),-8} {Trim(c.PayeeName, 20),-20} "
                + $"{c.LineAmount,12:0.00}  {Trim(c.CurrentAccount.ToString(), 24),-24} "
                + $"{Trim(c.ProposedAccount.ToString(), 24),-24} "
                + $"{(c.Cleared == ClearedStatus.Unknown ? "?" : c.Cleared.ToString()),-10} {c.StatusLabel}");

            foreach (var warning in c.Warnings.Where(w => w.Code == "UNSUPPORTED"))
            {
                Console.WriteLine($"    ! {warning.Message}");
            }
        }

        Console.WriteLine(new string('-', 150));
        Console.WriteLine(
            $"{plan.MatchCount} matching line(s) across {plan.TransactionsExamined} transaction(s) examined; "
            + $"total {plan.TotalAmount:0.00}.");

        if (plan.UnsupportedCount > 0)
        {
            Console.WriteLine($"{plan.UnsupportedCount} line(s) cannot be changed by this utility and are shown for review only.");
        }

        Console.WriteLine(plan.ClearedStatusUnavailable
            ? "Reconciliation status: NOT REPORTED by this QuickBooks for these records."
            : $"Reconciled or cleared: {plan.ReconciledCount} line(s).");
    }

    private static void PrintApproval(Batch batch, BatchApprovalSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("================ BATCH APPROVAL ================");
        Console.WriteLine($"Company file:      {summary.Company.CompanyName} ({summary.Company.CompanyFileName})");
        Console.WriteLine($"Rule:              {summary.SourceAccount} -> {summary.DestinationAccount}");
        Console.WriteLine($"Date range:        {summary.FromDate:yyyy-MM-dd} to {summary.ToDate:yyyy-MM-dd}");
        Console.WriteLine($"Transaction types: {string.Join(", ", summary.TransactionTypes)}");
        Console.WriteLine($"Batch:             {batch.Sequence}");
        Console.WriteLine($"Transactions:      {summary.TransactionCount}");
        Console.WriteLine($"Transaction lines: {summary.LineCount}");
        Console.WriteLine($"Total amount:      {summary.TotalAmount:0.00}");
        Console.WriteLine($"Reconciled/cleared:{summary.ReconciledCount}");
        Console.WriteLine($"Warnings:          {summary.WarningCount}");
        Console.WriteLine($"Unsupported:       {summary.UnsupportedCount}");
        Console.WriteLine($"Backup confirmed:  {summary.BackupConfirmedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "NOT CONFIRMED"}");
        Console.WriteLine();
        Console.WriteLine(summary.ScopeStatement);
        Console.WriteLine("================================================");
    }

    private static bool Confirm(CommandLine cli, BatchApprovalSummary summary)
    {
        if (cli.Has("yes"))
        {
            Console.WriteLine($"[--yes] {summary.ButtonText}");
            return true;
        }

        Console.Write($"Type APPLY to \"{summary.ButtonText}\", or anything else to stop: ");
        return string.Equals(Console.ReadLine()?.Trim(), "APPLY", StringComparison.Ordinal);
    }

    private static string Trim(string? value, int width) =>
        value is null ? string.Empty : value.Length <= width ? value : value[..(width - 1)] + "~";

    private static SqliteAuditStore OpenStore(CommandLine cli) =>
        cli.Get("db") is { } path ? new SqliteAuditStore(path) : SqliteAuditStore.OpenDefault();

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        return 1;
    }

    /// <summary>Session, store and services for one command.</summary>
    private sealed class Context : IDisposable
    {
        private Context(IQbSession session, SqliteAuditStore store)
        {
            Session = session;
            Store = store;
            Registry = AdapterRegistry.Default;
            Query = new QueryService(session, Registry);
            Planner = new ReclassificationPlanner(Registry);
            Preflight = new PreflightService(Query, Registry);
            Verification = new VerificationService(Query);
            Batches = new BatchPlanner();
            Execution = new ExecutionService(session, Query, Preflight, Verification, Registry, Store);
            Reconciliation = new ReconciliationService(Query, Store, Console.Error.WriteLine);
            Export = new ExportService(Store);
        }

        public IQbSession Session { get; }
        public SqliteAuditStore Store { get; }
        public AdapterRegistry Registry { get; }
        public QueryService Query { get; }
        public ReclassificationPlanner Planner { get; }
        public PreflightService Preflight { get; }
        public VerificationService Verification { get; }
        public BatchPlanner Batches { get; }
        public ExecutionService Execution { get; }
        public ReconciliationService Reconciliation { get; }
        public ExportService Export { get; }

        public static Context Open(CommandLine cli, bool requireWrite = false)
        {
            var writable = requireWrite || cli.Has("allow-write");
            var store = OpenStore(cli);

            try
            {
                return new Context(OpenSession(cli, writable), store);
            }
            catch
            {
                store.Dispose();
                throw;
            }
        }

        private static IQbSession OpenSession(CommandLine cli, bool writable)
        {
            if (cli.Has("simulate"))
            {
                var company = BuildDemoCompany(out _, out _);
                return new SimulatedQbSession(company, readOnly: !writable);
            }

#if QB_LIVE_SESSION
            return QuickBooks.QbComSession.Connect(new QuickBooks.QbConnectionOptions
            {
                CompanyFilePath = cli.Get("company") ?? string.Empty,
                ConnectionType = cli.Has("launch")
                    ? QuickBooks.QbConnectionType.LocalLaunchIfNeeded
                    : QuickBooks.QbConnectionType.LocalAlreadyRunning,
                ReadOnly = !writable,
            });
#else
            throw new QbSessionException(
                "A live QuickBooks session is only available in a Windows build. Use --simulate to run "
                + "against the built-in simulated company.");
#endif
        }

        public void Dispose()
        {
            Session.Dispose();
            Store.Dispose();
        }
    }
}
