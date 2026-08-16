using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Session;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>The controls listed in spec section 10, "Safety and Accounting Integrity Requirements".</summary>
public sealed class SafetyTests
{
    /// <summary>Section 10, "Backup confirmation": no write session before the user confirms one.</summary>
    [Fact]
    public void WithoutBackupConfirmation_ExecutionIsRefused()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, confirmBackup: false);
        var plan = harness.Preview(job);

        harness.Store.SetSelection(plan.Candidates.Where(c => c.IsSelectable).Select(c => c.CandidateId), true);
        var selected = harness.Store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batch = harness.Batches.SplitIntoBatches(job, selected).First();

        // The batch cannot even be approved.
        var approvalError = Assert.Throws<InvalidOperationException>(() => harness.Batches.Approve(batch, "test"));
        Assert.Contains("backup", approvalError.Message, StringComparison.OrdinalIgnoreCase);

        // And forcing an approved-looking batch past that still fails at execution.
        var forged = batch with { Status = BatchStatus.Approved, ApprovedAt = DateTimeOffset.UtcNow };
        var executionError = Assert.Throws<InvalidOperationException>(() => harness.Execution.Execute(job, forged));
        Assert.Contains("backup", executionError.Message, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(harness.Session.SentRequests, r => r.Contains("ModRq", StringComparison.Ordinal));
    }

    /// <summary>Section 10, "Company-file lock": a job refuses to run against a different company.</summary>
    [Fact]
    public void AgainstADifferentCompanyFile_ExecutionIsRefused()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));

        // The user switched company files while the preview was on screen.
        company.Identity = company.Identity with
        {
            CompanyName = "A Different Company LLC",
            LegalCompanyName = "A Different Company LLC",
            CompanyFileName = @"C:\QuickBooks\Other.QBW",
        };

        var error = Assert.Throws<QbCompanyMismatchException>(() => harness.Execution.Execute(job, batch));
        Assert.Equal(job.Company.Fingerprint, error.Expected.Fingerprint);
        Assert.DoesNotContain(harness.Session.SentRequests, r => r.Contains("ModRq", StringComparison.Ordinal));
    }

    /// <summary>FR-002: the application's read-only mode really is read-only.</summary>
    [Fact]
    public void AReadOnlySession_RefusesToExecute()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company, readOnly: true);

        var job = harness.NewJob(source, destination);
        var plan = harness.Preview(job);

        // Preview works: it is a read.
        Assert.NotEmpty(plan.Candidates);

        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));
        var error = Assert.Throws<InvalidOperationException>(() => harness.Execution.Execute(job, batch));
        Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Establishes that line preservation is a real hazard rather than a vacuous guarantee: a
    /// modification that omits a line deletes it. The adapter's job is never to build one.
    /// </summary>
    [Fact]
    public void AModificationThatOmitsALine_DeletesIt()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        using var session = new SimulatedQbSession(company);

        var txn = company.Transactions.Single(t => t.RefNumber == "1002");
        Assert.Equal(2, txn.Lines.Count);

        // A deliberately careless request: only the line being changed is mentioned.
        var careless = new System.Xml.Linq.XElement("CreditCardChargeModRq",
            new System.Xml.Linq.XAttribute("requestID", "1"),
            new System.Xml.Linq.XElement("CreditCardChargeMod",
                new System.Xml.Linq.XElement("TxnID", txn.TxnId),
                new System.Xml.Linq.XElement("EditSequence", txn.EditSequence),
                new System.Xml.Linq.XElement("ExpenseLineMod",
                    new System.Xml.Linq.XElement("TxnLineID", txn.Lines[0].TxnLineId),
                    new System.Xml.Linq.XElement("AccountRef",
                        new System.Xml.Linq.XElement("ListID", destination.ListId)),
                    new System.Xml.Linq.XElement("Amount", "118.00"))));

        var response = QbXmlResponseParser.ParseEnvelope(
            session.SendRequest(QbXmlRequestBuilder.Envelope(careless, "16.0")));

        Assert.True(response.Status.IsOk);

        var after = company.Transactions.Single(t => t.TxnId == txn.TxnId);
        Assert.Single(after.Lines);
        Assert.Equal(118.00m, after.TotalAmount);
    }

    /// <summary>
    /// The counterpart: the adapter's request mentions every line, so the second line survives.
    /// </summary>
    [Fact]
    public void TheAdapterAlwaysResubmitsEveryLine()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        var adapter = new CreditCardChargeAdapter();

        var txn = company.Transactions.Single(t => t.RefNumber == "1002");
        var request = adapter.BuildReclassification(
            txn,
            [txn.Lines[0].TxnLineId!],
            QbRef.FromAccount(destination));

        var mod = request.Element("CreditCardChargeMod")!;
        var lineMods = mod.Elements("ExpenseLineMod").ToList();

        Assert.Equal(txn.ExpenseLines.Count(), lineMods.Count);
        Assert.All(
            txn.ExpenseLines,
            line => Assert.Contains(lineMods, m => m.Element("TxnLineID")?.Value == line.TxnLineId));
    }

    /// <summary>
    /// FR-013: header fields are not resubmitted. Omitting them leaves QuickBooks' stored values
    /// untouched, which is a smaller mutation than echoing them back.
    /// </summary>
    [Fact]
    public void TheModificationRequestCarriesNoHeaderFields()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        var adapter = new CreditCardChargeAdapter();

        var txn = company.Transactions.Single(t => t.RefNumber == "1003");
        var mod = adapter
            .BuildReclassification(txn, [txn.ExpenseLines.First().TxnLineId!], QbRef.FromAccount(destination))
            .Element("CreditCardChargeMod")!;

        Assert.Null(mod.Element("TxnDate"));
        Assert.Null(mod.Element("RefNumber"));
        Assert.Null(mod.Element("Memo"));
        Assert.Null(mod.Element("PayeeEntityRef"));
        Assert.Null(mod.Element("AccountRef"));

        Assert.Equal(txn.TxnId, mod.Element("TxnID")?.Value);
        Assert.Equal(txn.EditSequence, mod.Element("EditSequence")?.Value);
    }

    /// <summary>Section 10, "No automatic delete/recreate": no adapter can emit a delete or add.</summary>
    [Fact]
    public void NoAdapterEmitsADeleteOrAddRequest()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(
            source,
            destination,
            types: [TransactionType.CreditCardCharge, TransactionType.Check]);

        harness.RunToCompletion(job);

        foreach (var forbidden in new[] { "TxnDelRq", "TxnVoidRq", "CreditCardChargeAddRq", "CheckAddRq", "CheckModRq" })
        {
            Assert.DoesNotContain(
                harness.Session.SentRequests,
                r => r.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    /// <summary>FR-018: an unsupported record shape is reported, never worked around.</summary>
    [Theory]
    [InlineData("itemgroup")]
    [InlineData("multicurrency")]
    [InlineData("taxinclusive")]
    public void UnsupportedRecordShapes_ArePreviewedAndRefused(string shape)
    {
        var company = new SimulatedCompany();
        var card = company.AddAccount("Business Visa", "CreditCard");
        var source = company.AddAccount("Office Expense", "Expense");
        var destination = company.AddAccount("Automobile:Fuel", "Expense");

        var txn = company.AddTransaction(
            TransactionType.CreditCardCharge,
            card,
            new DateOnly(2024, 2, 2),
            "Supplier",
            [(source, 100m, "line")],
            refNumber: "9001");

        switch (shape)
        {
            case "itemgroup":
                company.AddItemGroupLine(txn.TxnId, "Bundle", 25m);
                break;
            case "multicurrency":
                company.Replace(company.Find(txn.TxnId)! with { CurrencyCode = "EUR", ExchangeRate = 1.09m });
                break;
            case "taxinclusive":
                company.Replace(company.Find(txn.TxnId)! with { IsTaxIncluded = true });
                break;
        }

        using var harness = new Harness(company);
        var job = harness.NewJob(source, destination);
        var plan = harness.Preview(job);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(ModificationSupport.UnsupportedRecordShape, candidate.Support);
        Assert.False(candidate.IsSelectable);
        Assert.NotNull(candidate.UnsupportedReason);
        Assert.Equal(1, plan.UnsupportedCount);

        // The adapter refuses even if something bypasses the preview.
        var adapter = new CreditCardChargeAdapter();
        Assert.Throws<NotSupportedException>(() => adapter.BuildReclassification(
            company.Find(txn.TxnId)!,
            [candidate.TxnLineId],
            QbRef.FromAccount(destination)));
    }

    /// <summary>
    /// Section 9: when QuickBooks does not report a cleared status, the utility says so rather than
    /// implying nothing is reconciled.
    /// </summary>
    [Fact]
    public void WhenQuickBooksOmitsClearedStatus_ThePlanSaysItIsUnavailable()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        company.ReportsClearedStatus = false;

        using var harness = new Harness(company);
        var plan = harness.Preview(harness.NewJob(source, destination));

        Assert.True(plan.ClearedStatusUnavailable);
        Assert.Equal(0, plan.ReconciledCount);
        Assert.All(plan.Candidates, c => Assert.Equal(ClearedStatus.Unknown, c.Cleared));
    }

    /// <summary>FR-005: a destination that cannot carry an expense line is rejected before any read.</summary>
    [Fact]
    public void ABalanceSheetDestination_FailsJobValidation()
    {
        var company = TestCompanies.Standard(out var source, out _, out var creditCard);
        using var harness = new Harness(company);

        var errors = harness.NewJob(source, creditCard).Validate();

        Assert.Contains(errors, e => e.Contains("cannot carry an expense line", StringComparison.Ordinal));
    }

    /// <summary>FR-005: source and destination must differ.</summary>
    [Fact]
    public void SameSourceAndDestination_FailsJobValidation()
    {
        var company = TestCompanies.Standard(out var source, out _, out _);
        using var harness = new Harness(company);

        Assert.Contains(
            harness.NewJob(source, source).Validate(),
            e => e.Contains("must differ", StringComparison.Ordinal));
    }

    /// <summary>Section 9: the approval button names the action and the count, never "OK".</summary>
    [Fact]
    public void TheApprovalSummaryStatesScopeAndNamesTheAction()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));
        var summary = harness.Batches.Summarize(job, batch, harness.Store.GetCandidates(job.JobId));

        Assert.Equal(job.Company.CompanyName, summary.Company.CompanyName);
        Assert.Equal(source.FullName, summary.SourceAccount);
        Assert.Equal(destination.FullName, summary.DestinationAccount);
        Assert.True(summary.LineCount > 0);
        Assert.NotNull(summary.BackupConfirmedAt);
        Assert.Contains("will be changed", summary.ScopeStatement, StringComparison.Ordinal);
        Assert.StartsWith("Apply ", summary.ButtonText, StringComparison.Ordinal);
        Assert.Contains("Reclassification", summary.ButtonText, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-016: once a line has been written, no amount of clicking in the grid can queue it for a
    /// second write. "Select none" followed by "select all" must not resurrect completed work.
    /// </summary>
    [Fact]
    public void CompletedWorkCannotBeReselected()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        var (_, _, outcome) = harness.RunToCompletion(job);
        Assert.True(outcome.SucceededCount > 0);

        var verified = harness.Store.GetCandidates(job.JobId)
            .Where(c => c.Status == CandidateStatus.Verified)
            .Select(c => c.CandidateId)
            .ToList();

        Assert.NotEmpty(verified);

        var everyCandidate = harness.Store.GetCandidates(job.JobId).Select(c => c.CandidateId).ToList();
        harness.Store.SetSelection(everyCandidate, false);
        harness.Store.SetSelection(everyCandidate, true);

        foreach (var id in verified)
        {
            var candidate = harness.Store.GetCandidate(id)!;
            Assert.Equal(CandidateStatus.Verified, candidate.Status);
            Assert.False(candidate.IsSelected);
        }

        // And they are excluded from any batch built afterwards.
        var selected = harness.Store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batches = harness.Batches.SplitIntoBatches(job, selected);

        Assert.DoesNotContain(batches.SelectMany(b => b.CandidateIds), id => verified.Contains(id));
    }

    /// <summary>
    /// FR-010: batches respect the configured size, and a transaction's lines are never split
    /// across two batches.
    /// </summary>
    [Fact]
    public void BatchesRespectTheSizeLimitAndKeepTransactionsWhole()
    {
        var company = new SimulatedCompany();
        var card = company.AddAccount("Business Visa", "CreditCard");
        var source = company.AddAccount("Office Expense", "Expense");
        var destination = company.AddAccount("Automobile:Fuel", "Expense");

        for (var i = 0; i < 20; i++)
        {
            company.AddTransaction(
                TransactionType.CreditCardCharge,
                card,
                new DateOnly(2024, 1, 1).AddDays(i),
                $"Vendor {i}",
                [(source, 10m, "a"), (source, 20m, "b")],
                refNumber: $"R{i:D3}");
        }

        using var harness = new Harness(company);
        var job = harness.NewJob(source, destination, batchSize: 6);
        var plan = harness.Preview(job);

        harness.Store.SetSelection(plan.Candidates.Select(c => c.CandidateId), true);
        var selected = harness.Store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batches = harness.Batches.SplitIntoBatches(job, selected);

        Assert.Equal(40, selected.Count);
        Assert.All(batches, b => Assert.True(b.CandidateCount <= job.BatchSize));

        var candidatesById = plan.Candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in batches)
        {
            foreach (var id in batch.CandidateIds)
            {
                var txnId = candidatesById[id].TxnId;
                if (seen.TryGetValue(txnId, out var owner))
                {
                    Assert.Equal(owner, batch.BatchId);
                }
                else
                {
                    seen[txnId] = batch.BatchId;
                }
            }
        }

        Assert.Equal(40, batches.Sum(b => b.CandidateCount));
    }
}
