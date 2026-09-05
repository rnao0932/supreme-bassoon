using QbReclass.Core.Adapters;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;
using QbReclass.Core.Services;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>
/// The check write path, held to the same standard as credit card charges. Every scenario here
/// mirrors one in <see cref="ReclassificationTests"/>: enabling a transaction type must not come
/// with weaker guarantees than the type that was proven first.
/// </summary>
public sealed class CheckWriteTests
{
    /// <summary>A company whose checks carry the shapes that matter: multi-line, reconciled, classed.</summary>
    private static SimulatedCompany CheckCompany(out QbAccount source, out QbAccount destination)
    {
        var company = new SimulatedCompany { SupportsCheckMod = true };

        var bank = company.AddAccount("Operating Checking", "Bank");
        source = company.AddAccount("Ask My Accountant", "Expense");
        destination = company.AddAccount("Automobile:Fuel", "Expense");
        var other = company.AddAccount("Meals and Entertainment", "Expense");

        company.AddTransaction(
            TransactionType.Check, bank, new DateOnly(2024, 2, 14), "Valley Fuel",
            [(source, 412.00m, "bulk diesel")], refNumber: "2210");

        company.AddTransaction(
            TransactionType.Check, bank, new DateOnly(2024, 3, 2), "Costco",
            [(source, 118.00m, "fuel"), (other, 44.25m, "lunch")], refNumber: "2211");

        company.AddTransaction(
            TransactionType.Check, bank, new DateOnly(2024, 4, 9), "Shell",
            [(source, 71.15m, "fuel")], refNumber: "2212",
            cleared: ClearedStatus.Reconciled, className: "Field Ops");

        return company;
    }

    private static Harness Enabled(SimulatedCompany company) =>
        new(company, registry: AdapterRegistry.Create(enableCheckWrites: true));

    [Fact]
    public void WithCheckWritesEnabled_ASingleLineCheckIsReclassifiedAndVerified()
    {
        var company = CheckCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2210" });

        var (_, plan, outcome) = harness.RunToCompletion(job);

        Assert.Single(plan.Candidates);
        Assert.Equal(1, outcome.SucceededCount);
        Assert.False(outcome.Halted);

        var after = company.Transactions.Single(t => t.RefNumber == "2210");
        Assert.Equal(destination.ListId, after.ExpenseLines.Single().Account.ListId);
    }

    /// <summary>The hazard that matters most: the unrelated line must survive.</summary>
    [Fact]
    public void AMultiLineCheckKeepsItsOtherLines()
    {
        var company = CheckCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var before = company.Transactions.Single(t => t.RefNumber == "2211");
        Assert.Equal(2, before.Lines.Count);

        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2211" });

        var (_, _, outcome) = harness.RunToCompletion(job);
        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.RefNumber == "2211");
        Assert.Equal(2, after.Lines.Count);
        Assert.Equal(before.TotalAmount, after.TotalAmount);

        var untouchedBefore = before.Lines.Single(l => l.Memo == "lunch");
        var untouchedAfter = after.Lines.Single(l => l.Memo == "lunch");
        Assert.Equal(untouchedBefore.Account.ListId, untouchedAfter.Account.ListId);
        Assert.Equal(untouchedBefore.Amount, untouchedAfter.Amount);
    }

    /// <summary>
    /// A check's number and its bank account are protected. Neither is sent in the request at all,
    /// so a reclassification cannot renumber a check or move it between bank accounts.
    /// </summary>
    [Fact]
    public void TheCheckNumberAndBankAccountAreNeverSent()
    {
        var company = CheckCompany(out _, out var destination);
        var txn = company.Transactions.Single(t => t.RefNumber == "2211");

        var mod = new CheckAdapter(enableModification: true)
            .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination))
            .Element("CheckMod")!;

        Assert.Null(mod.Element("RefNumber"));
        Assert.Null(mod.Element("AccountRef"));
        Assert.Null(mod.Element("TxnDate"));
        Assert.Null(mod.Element("PayeeEntityRef"));
        Assert.Null(mod.Element("IsToBePrinted"));

        Assert.Equal("TxnID", mod.Elements().First().Name.LocalName);
        Assert.Equal("EditSequence", mod.Elements().ElementAt(1).Name.LocalName);
    }

    [Fact]
    public void AReconciledCheckIsFlaggedAndItsClearedStatusSurvives()
    {
        var company = CheckCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2212" });

        var (_, plan, outcome) = harness.RunToCompletion(job);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Contains(candidate.Warnings, w => w.Code == "RECONCILED");
        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.RefNumber == "2212");
        Assert.Equal(ClearedStatus.Reconciled, after.Cleared);
        Assert.Equal("Field Ops", after.ExpenseLines.Single().Class.FullName);
    }

    [Fact]
    public void AStaleCheckIsSkippedNotForced()
    {
        var company = CheckCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2210" });

        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);

        var target = company.Transactions.Single(t => t.RefNumber == "2210");
        company.EditExternally(target.TxnId, t => t with { Memo = "edited elsewhere" });

        var outcome = harness.Execution.Execute(job, batch);

        Assert.Equal(0, outcome.SucceededCount);
        Assert.Equal(1, outcome.SkippedCount);
        Assert.Equal(source.ListId, company.Find(target.TxnId)!.ExpenseLines.Single().Account.ListId);
    }

    /// <summary>
    /// Enabling the write path does not license delete-and-recreate. The prohibition is absolute,
    /// not a consequence of checks having been unwritable.
    /// </summary>
    [Fact]
    public void EvenWithWritesEnabled_NoDeleteOrRecreateIsEverSent()
    {
        var company = CheckCompany(out var source, out var destination);
        using var harness = Enabled(company);

        harness.RunToCompletion(harness.NewJob(source, destination, types: [TransactionType.Check]));

        foreach (var forbidden in new[] { "TxnDelRq", "TxnVoidRq", "CheckAddRq" })
        {
            Assert.DoesNotContain(
                harness.Session.SentRequests, r => r.Contains(forbidden, StringComparison.Ordinal));
        }

        Assert.Contains(harness.Session.SentRequests, r => r.Contains("CheckModRq", StringComparison.Ordinal));
    }

    /// <summary>
    /// An edition that does not implement CheckModRq refuses the write, and the utility records the
    /// failure rather than falling back to anything else.
    /// </summary>
    [Fact]
    public void WhenQuickBooksRefusesCheckMod_TheFailureIsRecordedNotWorkedAround()
    {
        var company = CheckCompany(out var source, out var destination);
        company.SupportsCheckMod = false;

        using var harness = Enabled(company);
        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2210" });

        var (_, _, outcome) = harness.RunToCompletion(job);

        Assert.Equal(0, outcome.SucceededCount);
        var result = Assert.Single(outcome.Results);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(500, result.StatusCode);

        Assert.Equal(source.ListId, company.Transactions
            .Single(t => t.RefNumber == "2210").ExpenseLines.Single().Account.ListId);
    }

    /// <summary>The default build still refuses, so nothing changes for anyone who does not opt in.</summary>
    [Fact]
    public void TheDefaultRegistryStillTreatsChecksAsPreviewOnly()
    {
        Assert.DoesNotContain(TransactionType.Check, AdapterRegistry.Default.WritableTypes);
        Assert.Contains(TransactionType.Check, AdapterRegistry.Default.QueryableTypes);

        var adapter = AdapterRegistry.Default.Get(TransactionType.Check);
        Assert.Equal(ModificationSupport.NotSupported, adapter.TypeSupport);
        Assert.Null(adapter.ModRequestName);

        var company = CheckCompany(out _, out var destination);
        var txn = company.Transactions.First();

        var error = Assert.Throws<NotSupportedException>(() => adapter.BuildReclassification(
            txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination)));

        Assert.Contains("not enabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheEnabledRegistryReportsChecksAsWritable()
    {
        var registry = AdapterRegistry.Create(enableCheckWrites: true);

        Assert.Contains(TransactionType.Check, registry.WritableTypes);
        Assert.Equal("CheckModRq", registry.Get(TransactionType.Check).ModRequestName);
    }

    /// <summary>Record shapes are refused for checks on the same terms as for charges.</summary>
    [Fact]
    public void UnsupportedShapesAreRefusedForChecksToo()
    {
        var company = CheckCompany(out var source, out var destination);
        var txn = company.Transactions.Single(t => t.RefNumber == "2210");
        company.AddItemGroupLine(txn.TxnId, "Bundle", 25m);

        using var harness = Enabled(company);
        var job = harness.NewJob(
            source, destination,
            types: [TransactionType.Check],
            filters: new JobFilters { RefNumberContains = "2210" });

        var plan = harness.Preview(job);
        var candidate = Assert.Single(plan.Candidates);

        Assert.Equal(ModificationSupport.UnsupportedRecordShape, candidate.Support);
        Assert.False(candidate.IsSelectable);
    }
}
