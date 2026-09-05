using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Services;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>
/// Bills, held to the same standard as the types proven before them, plus the two things that are
/// specific to bills: they post to Accounts Payable, and they can already have been paid.
/// </summary>
public sealed class BillWriteTests
{
    private static SimulatedCompany BillCompany(out QbAccount source, out QbAccount destination)
    {
        var company = new SimulatedCompany { SupportsBillMod = true };

        var payable = company.AddAccount("Accounts Payable", "AccountsPayable");
        source = company.AddAccount("Ask My Accountant", "Expense");
        destination = company.AddAccount("Professional Fees", "Expense");
        var other = company.AddAccount("Office Supplies", "Expense");

        company.AddTransaction(
            TransactionType.Bill, payable, new DateOnly(2024, 2, 6), "Westlaw",
            [(source, 1250.00m, "research")], refNumber: "INV-88");

        company.AddTransaction(
            TransactionType.Bill, payable, new DateOnly(2024, 3, 18), "Staples",
            [(source, 310.00m, "binders"), (other, 92.40m, "paper")], refNumber: "INV-91");

        company.AddTransaction(
            TransactionType.Bill, payable, new DateOnly(2024, 4, 1), "Ricoh",
            [(source, 480.00m, "copier lease")], refNumber: "INV-95", isPaid: true);

        return company;
    }

    private static Harness Enabled(SimulatedCompany company) =>
        new(company, registry: AdapterRegistry.Create(enableCheckWrites: false, enableBillWrites: true));

    private static Job BillJob(Harness harness, QbAccount source, QbAccount destination, string? refFilter = null) =>
        harness.NewJob(
            source, destination,
            types: [TransactionType.Bill],
            filters: refFilter is null ? new JobFilters() : new JobFilters { RefNumberContains = refFilter });

    [Fact]
    public void WithBillWritesEnabled_ABillIsReclassifiedAndVerified()
    {
        var company = BillCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var (_, plan, outcome) = harness.RunToCompletion(BillJob(harness, source, destination, "INV-88"));

        Assert.Single(plan.Candidates);
        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.RefNumber == "INV-88");
        Assert.Equal(destination.ListId, after.ExpenseLines.Single().Account.ListId);
    }

    [Fact]
    public void AMultiLineBillKeepsItsOtherLines()
    {
        var company = BillCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var before = company.Transactions.Single(t => t.RefNumber == "INV-91");
        harness.RunToCompletion(BillJob(harness, source, destination, "INV-91"));
        var after = company.Transactions.Single(t => t.RefNumber == "INV-91");

        Assert.Equal(2, after.Lines.Count);
        Assert.Equal(before.TotalAmount, after.TotalAmount);

        var untouched = after.Lines.Single(l => l.Memo == "paper");
        Assert.Equal(before.Lines.Single(l => l.Memo == "paper").Account.ListId, untouched.Account.ListId);
    }

    /// <summary>
    /// A bill's Accounts Payable account is a header field and is never sent, so a reclassification
    /// cannot move a bill between AP accounts.
    /// </summary>
    [Fact]
    public void TheAccountsPayableAccountAndVendorAreNeverSent()
    {
        var company = BillCompany(out _, out var destination);
        var txn = company.Transactions.Single(t => t.RefNumber == "INV-91");

        var mod = new BillAdapter(enableModification: true)
            .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination))
            .Element("BillMod")!;

        Assert.Null(mod.Element("APAccountRef"));
        Assert.Null(mod.Element("AccountRef"));
        Assert.Null(mod.Element("VendorRef"));
        Assert.Null(mod.Element("TxnDate"));
        Assert.Null(mod.Element("DueDate"));
        Assert.Null(mod.Element("RefNumber"));

        Assert.Equal("TxnID", mod.Elements().First().Name.LocalName);
        Assert.Equal("EditSequence", mod.Elements().ElementAt(1).Name.LocalName);
    }

    /// <summary>
    /// A paid bill can still be reclassified - that is often exactly the correction wanted - but the
    /// operator sees that they are changing what a settled payment was for.
    /// </summary>
    [Fact]
    public void APaidBillIsFlaggedButStillWritable()
    {
        var company = BillCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var (_, plan, outcome) = harness.RunToCompletion(BillJob(harness, source, destination, "INV-95"));

        var candidate = Assert.Single(plan.Candidates);
        Assert.Contains(candidate.Warnings, w => w.Code == "PAID");
        Assert.True(candidate.IsSelectable);
        Assert.Equal(1, outcome.SucceededCount);
    }

    /// <summary>QuickBooks reports a bill's AP account as APAccountRef, not AccountRef.</summary>
    [Fact]
    public void ThePayableAccountIsReadFromApAccountRef()
    {
        var company = BillCompany(out _, out _);
        using var session = new SimulatedQbSession(company);

        var original = company.Transactions.Single(t => t.RefNumber == "INV-88");
        var response = QbXmlResponseParser.ParseEnvelope(session.SendRequest(
            QbXmlRequestBuilder.Envelope(
                QbXmlRequestBuilder.TransactionQueryByTxnId(TransactionType.Bill, original.TxnId), "16.0")));

        var parsed = Assert.Single(QbXmlResponseParser.ParseTransactions(response, TransactionType.Bill));

        Assert.Equal("Accounts Payable", parsed.PostingAccount.FullName);
        Assert.Equal(original.PostingAccount.ListId, parsed.PostingAccount.ListId);
    }

    [Fact]
    public void AStaleBillIsSkippedNotForced()
    {
        var company = BillCompany(out var source, out var destination);
        using var harness = Enabled(company);

        var job = BillJob(harness, source, destination, "INV-88");
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);

        var target = company.Transactions.Single(t => t.RefNumber == "INV-88");
        company.EditExternally(target.TxnId, t => t with { Memo = "touched" });

        var outcome = harness.Execution.Execute(job, batch);

        Assert.Equal(1, outcome.SkippedCount);
        Assert.Equal(source.ListId, company.Find(target.TxnId)!.ExpenseLines.Single().Account.ListId);
    }

    [Fact]
    public void TheDefaultRegistryTreatsBillsAsPreviewOnly()
    {
        Assert.DoesNotContain(TransactionType.Bill, AdapterRegistry.Default.WritableTypes);
        Assert.Contains(TransactionType.Bill, AdapterRegistry.Default.QueryableTypes);

        var adapter = AdapterRegistry.Default.Get(TransactionType.Bill);
        Assert.Equal(ModificationSupport.NotSupported, adapter.TypeSupport);
        Assert.Null(adapter.ModRequestName);
    }

    /// <summary>Enabling one unproven type must not enable the other.</summary>
    [Fact]
    public void TheTypeFlagsAreIndependent()
    {
        var billsOnly = AdapterRegistry.Create(enableCheckWrites: false, enableBillWrites: true);
        Assert.Contains(TransactionType.Bill, billsOnly.WritableTypes);
        Assert.DoesNotContain(TransactionType.Check, billsOnly.WritableTypes);

        var checksOnly = AdapterRegistry.Create(enableCheckWrites: true, enableBillWrites: false);
        Assert.Contains(TransactionType.Check, checksOnly.WritableTypes);
        Assert.DoesNotContain(TransactionType.Bill, checksOnly.WritableTypes);

        // Credit card charges are proven and never gated.
        Assert.Contains(TransactionType.CreditCardCharge, AdapterRegistry.Default.WritableTypes);
    }

    [Fact]
    public void WhenQuickBooksRefusesBillMod_TheFailureIsRecordedNotWorkedAround()
    {
        var company = BillCompany(out var source, out var destination);
        company.SupportsBillMod = false;

        using var harness = Enabled(company);
        var (_, _, outcome) = harness.RunToCompletion(BillJob(harness, source, destination, "INV-88"));

        var result = Assert.Single(outcome.Results);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Equal(500, result.StatusCode);

        Assert.DoesNotContain(
            harness.Session.SentRequests, r => r.Contains("BillAddRq", StringComparison.Ordinal));
    }
}
