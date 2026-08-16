using QbReclass.Core.Model;
using QbReclass.Core.Services;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>
/// The core write-path scenarios from spec section 18 ("Testing Plan") and the version 1
/// acceptance criteria in section 19.
/// </summary>
public sealed class ReclassificationTests
{
    /// <summary>Spec section 18: single-line credit-card charge reclassification.</summary>
    [Fact]
    public void SingleLineCharge_IsReclassifiedAndVerified()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1001" });
        var (_, plan, outcome) = harness.RunToCompletion(job);

        Assert.Single(plan.Candidates);
        Assert.Equal(1, outcome.SucceededCount);
        Assert.False(outcome.Halted);

        var txn = company.Transactions.Single(t => t.RefNumber == "1001");
        Assert.Equal(destination.ListId, txn.ExpenseLines.Single().Account.ListId);
    }

    /// <summary>
    /// Spec section 18: multi-line charge where only one expense line changes. This is the test the
    /// spec's "line preservation" control exists for.
    /// </summary>
    [Fact]
    public void MultiLineCharge_ChangesOnlyTargetLine_AndRetainsTheOthers()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var before = company.Transactions.Single(t => t.RefNumber == "1002");
        Assert.Equal(2, before.Lines.Count);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1002" });
        var (_, plan, outcome) = harness.RunToCompletion(job);

        Assert.Single(plan.Candidates);
        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.RefNumber == "1002");

        Assert.Equal(2, after.Lines.Count);
        Assert.Equal(before.TotalAmount, after.TotalAmount);

        var changed = after.Lines.Single(l => l.Memo == "printer paper");
        var untouched = after.Lines.Single(l => l.Memo == "team lunch");

        Assert.Equal(destination.ListId, changed.Account.ListId);
        Assert.Equal(118.00m, changed.Amount);

        var untouchedBefore = before.Lines.Single(l => l.Memo == "team lunch");
        Assert.Equal(untouchedBefore.Account.ListId, untouched.Account.ListId);
        Assert.Equal(untouchedBefore.Amount, untouched.Amount);
        Assert.Equal(untouchedBefore.TxnLineId, untouched.TxnLineId);
    }

    /// <summary>
    /// Spec section 18: multiple lines on the same transaction selected for reclassification. They
    /// must travel in one modification request, or the second would hit a stale EditSequence.
    /// </summary>
    [Fact]
    public void MultipleTargetLinesOnOneTransaction_AreAppliedInASingleRequest()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1004" });
        var (_, plan, outcome) = harness.RunToCompletion(job);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal(2, outcome.SucceededCount);
        Assert.Equal(0, outcome.FailedCount);

        var modRequests = harness.Session.SentRequests
            .Count(r => r.Contains("CreditCardChargeModRq", StringComparison.Ordinal));
        Assert.Equal(1, modRequests);

        var after = company.Transactions.Single(t => t.RefNumber == "1004");
        Assert.All(after.ExpenseLines, l => Assert.Equal(destination.ListId, l.Account.ListId));
        Assert.Equal(55.00m, after.TotalAmount);
    }

    /// <summary>Spec section 18: charge carrying memo, class, payee and reference data.</summary>
    [Fact]
    public void ChargeWithMemoClassPayeeAndRef_KeepsAllOfThem()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var before = company.Transactions.Single(t => t.RefNumber == "1003");

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1003" });
        var (_, _, outcome) = harness.RunToCompletion(job);

        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.TxnId == before.TxnId);

        Assert.Equal(before.RefNumber, after.RefNumber);
        Assert.Equal(before.Memo, after.Memo);
        Assert.Equal(before.Payee, after.Payee);
        Assert.Equal(before.TxnDate, after.TxnDate);
        Assert.Equal(before.PostingAccount, after.PostingAccount);
        Assert.Equal(before.TotalAmount, after.TotalAmount);

        var beforeLine = before.ExpenseLines.Single();
        var afterLine = after.ExpenseLines.Single();
        Assert.Equal(beforeLine.Memo, afterLine.Memo);
        Assert.Equal(beforeLine.Class, afterLine.Class);
        Assert.Equal(beforeLine.Amount, afterLine.Amount);
    }

    /// <summary>
    /// Spec section 18: historical cleared/reconciled transaction. It is reclassified, flagged as
    /// high risk in the preview, and its cleared status is left alone.
    /// </summary>
    [Fact]
    public void ReconciledCharge_IsFlagged_AndItsClearedStatusSurvives()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1003" });
        var (_, plan, outcome) = harness.RunToCompletion(job);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal(ClearedStatus.Reconciled, candidate.Cleared);
        Assert.Contains(candidate.Warnings, w => w.Code == "RECONCILED");
        Assert.Equal(1, plan.ReconciledCount);

        Assert.Equal(1, outcome.SucceededCount);

        var after = company.Transactions.Single(t => t.RefNumber == "1003");
        Assert.Equal(ClearedStatus.Reconciled, after.Cleared);
    }

    /// <summary>
    /// Spec section 18: transaction edited in QuickBooks after preview but before execution. The
    /// utility must detect the conflict and skip, never force the write (FR-012).
    /// </summary>
    [Fact]
    public void TransactionEditedAfterPreview_IsSkippedAsStale_AndLeftUnchanged()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1001" });
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);

        var target = company.Transactions.Single(t => t.RefNumber == "1001");
        company.EditExternally(target.TxnId, t => t with { Memo = "edited by someone else" });

        var outcome = harness.Execution.Execute(job, batch);

        Assert.Equal(0, outcome.SucceededCount);
        Assert.Equal(1, outcome.SkippedCount);

        var result = outcome.Results.Single();
        Assert.Equal(ExecutionOutcome.Skipped, result.Outcome);
        Assert.Contains(nameof(PreflightFailure.Stale), result.Detail, StringComparison.Ordinal);

        var after = company.Transactions.Single(t => t.RefNumber == "1001");
        Assert.Equal(source.ListId, after.ExpenseLines.Single().Account.ListId);
        Assert.DoesNotContain(harness.Session.SentRequests, r => r.Contains("CreditCardChargeModRq", StringComparison.Ordinal));
    }

    /// <summary>
    /// Spec section 18: destination account made inactive after preview. Every candidate is skipped
    /// with an explanation, and nothing is written.
    /// </summary>
    [Fact]
    public void DestinationAccountMadeInactiveAfterPreview_SkipsEverything()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));

        company.DeactivateAccount(destination.ListId);

        var outcome = harness.Execution.Execute(job, batch);

        Assert.Equal(0, outcome.SucceededCount);
        Assert.True(outcome.SkippedCount > 0);
        Assert.All(outcome.Results, r =>
            Assert.Contains("inactive", r.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(harness.Session.SentRequests, r => r.Contains("CreditCardChargeModRq", StringComparison.Ordinal));
    }

    /// <summary>
    /// Spec section 18: QuickBooks closed during a batch. The batch halts, state is persisted, and
    /// already-successful work is not repeated (FR-015).
    /// </summary>
    [Fact]
    public void SessionLostMidBatch_HaltsAndPreservesCompletedWork()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, batchSize: 100);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));

        // Let the first transaction complete, then pull the connection.
        var requestsBeforeDrop = 0;
        harness.Session.OnRequest = (name, _) =>
        {
            if (name == "CreditCardChargeModRq")
            {
                requestsBeforeDrop++;
            }
        };
        harness.Session.DropSessionOnRequest = 8;

        var outcome = harness.Execution.Execute(job, batch);

        Assert.True(outcome.Halted);
        Assert.Contains("connection lost", outcome.HaltReason!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(outcome.UnattemptedCandidateIds);

        var storedBatch = harness.Store.GetBatch(batch.BatchId)!;
        Assert.Equal(BatchStatus.Halted, storedBatch.Status);

        // Everything recorded as verified really is at the destination in QuickBooks.
        var verified = harness.Store.GetCandidates(job.JobId)
            .Where(c => c.Status == CandidateStatus.Verified)
            .ToList();

        foreach (var candidate in verified)
        {
            var txn = company.Find(candidate.TxnId)!;
            Assert.Equal(destination.ListId, txn.FindLine(candidate.TxnLineId)!.Account.ListId);
        }
    }

    /// <summary>
    /// Spec section 18 and FR-016: the application is terminated after a successful write but
    /// before the completion flag is stored. On restart, reconciliation must recognize the work as
    /// done and not repeat it.
    /// </summary>
    [Fact]
    public void InterruptedAfterWrite_ReconciliationMarksComplete_AndDoesNotReapply()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1001" });
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);

        var candidate = plan.Candidates.Single();
        var target = company.Transactions.Single(t => t.RefNumber == "1001");

        // Simulate the crash window: the store recorded the submission, QuickBooks applied the
        // change, and the process died before the result was written back.
        harness.Store.MarkSubmitted(candidate.CandidateId, batch.BatchId, "CreditCardChargeModRq");
        company.EditExternally(target.TxnId, t => t with
        {
            Lines = t.Lines
                .Select(l => l.TxnLineId == candidate.TxnLineId
                    ? l with { Account = QbRef.FromAccount(destination) }
                    : l)
                .ToList(),
        });

        var stored = harness.Store.GetCandidate(candidate.CandidateId)!;
        Assert.Equal(CandidateStatus.Submitted, stored.Status);

        var reconciliation = harness.Reconciliation.Reconcile(job);

        Assert.Equal(1, reconciliation.AppliedCount);
        Assert.False(reconciliation.NeedsAttention);
        Assert.Equal(CandidateStatus.Verified, harness.Store.GetCandidate(candidate.CandidateId)!.Status);

        // Re-running the batch must not touch the record a second time.
        var modsBefore = harness.Session.SentRequests.Count(r => r.Contains("CreditCardChargeModRq", StringComparison.Ordinal));
        var rerun = harness.Execution.Execute(job, batch);
        var modsAfter = harness.Session.SentRequests.Count(r => r.Contains("CreditCardChargeModRq", StringComparison.Ordinal));

        Assert.Equal(modsBefore, modsAfter);
        Assert.Empty(rerun.Results);
    }

    /// <summary>
    /// The other half of the interruption case: the write never landed. Reconciliation must return
    /// the candidate to the pool deselected rather than silently retrying it.
    /// </summary>
    [Fact]
    public void InterruptedBeforeWrite_ReconciliationReturnsCandidateDeselected()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, filters: new JobFilters { RefNumberContains = "1001" });
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);
        var candidate = plan.Candidates.Single();

        harness.Store.MarkSubmitted(candidate.CandidateId, batch.BatchId, "CreditCardChargeModRq");

        var reconciliation = harness.Reconciliation.Reconcile(job);

        Assert.Equal(1, reconciliation.NotAppliedCount);
        Assert.False(reconciliation.NeedsAttention);

        var after = harness.Store.GetCandidate(candidate.CandidateId)!;
        Assert.Equal(CandidateStatus.Matched, after.Status);
        Assert.False(after.IsSelected);
    }

    /// <summary>
    /// Spec section 11 and FR-018: checks appear in the preview, are clearly unsupported, cannot be
    /// selected, and are never written.
    /// </summary>
    [Fact]
    public void Checks_ArePreviewedAsUnsupported_AndNeverWritten()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(
            source,
            destination,
            types: [TransactionType.CreditCardCharge, TransactionType.Check]);

        var plan = harness.Preview(job);

        var check = Assert.Single(plan.Candidates, c => c.TxnType == TransactionType.Check);
        Assert.Equal(ModificationSupport.NotSupported, check.Support);
        Assert.False(check.IsSelectable);
        Assert.Contains("CheckMod", check.UnsupportedReason!, StringComparison.Ordinal);
        Assert.Equal("Unsupported type", check.StatusLabel);

        // Even an explicit attempt to select it is refused.
        harness.Store.SetSelection([check.CandidateId], true);
        Assert.False(harness.Store.GetCandidate(check.CandidateId)!.IsSelected);

        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));
        Assert.DoesNotContain(check.CandidateId, batch.CandidateIds);

        harness.Execution.Execute(job, batch);
        Assert.DoesNotContain(harness.Session.SentRequests, r => r.Contains("CheckModRq", StringComparison.Ordinal));

        var checkTxn = company.Transactions.Single(t => t.TxnType == TransactionType.Check);
        Assert.Equal(source.ListId, checkTxn.ExpenseLines.Single().Account.ListId);
    }

    /// <summary>
    /// Spec section 15: a verification mismatch stops the batch immediately, whatever the stop
    /// policy says about ordinary failures.
    /// </summary>
    [Fact]
    public void VerificationMismatch_HaltsTheBatchEvenWhenContinuingOnFailures()
    {
        var company = new SimulatedCompany();
        var card = company.AddAccount("Business Visa", "CreditCard");
        var source = company.AddAccount("Office Expense", "Expense");
        var destination = company.AddAccount("Automobile:Fuel", "Expense");

        for (var i = 1; i <= 3; i++)
        {
            company.AddTransaction(
                TransactionType.CreditCardCharge,
                card,
                new DateOnly(2024, 3, i),
                $"Vendor {i}",
                [(source, 10m * i, $"line {i}")],
                refNumber: $"300{i}");
        }

        using var harness = new Harness(company);
        var job = harness.NewJob(source, destination, stopPolicy: StopPolicy.ContinueOnIsolatedFailure);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates);

        // QuickBooks accepts the write but quietly changes the date as well.
        harness.Session.OnRequest = (name, sim) =>
        {
            if (name != "CreditCardChargeModRq")
            {
                return;
            }

            harness.Session.OnRequest = null;
            var first = sim.Transactions.First();
            sim.Replace(first with { TxnDate = first.TxnDate.AddDays(-30) });
        };

        var outcome = harness.Execution.Execute(job, batch);

        Assert.Equal(1, outcome.MismatchCount);
        Assert.True(outcome.Halted);
        Assert.Contains("Verification mismatch", outcome.HaltReason!, StringComparison.Ordinal);
        Assert.NotEmpty(outcome.UnattemptedCandidateIds);
    }

    /// <summary>Spec section 10: the batch stops after the current transaction when asked to.</summary>
    [Fact]
    public void CancellationBetweenTransactions_StopsCleanly()
    {
        var company = TestCompanies.Standard(out var source, out var destination, out _);
        using var harness = new Harness(company);

        var job = harness.NewJob(source, destination, batchSize: 100);
        var plan = harness.Preview(job);
        var batch = harness.ApproveBatch(job, plan.Candidates.Where(c => c.IsSelectable));

        using var cts = new CancellationTokenSource();
        harness.Session.OnRequest = (name, _) =>
        {
            if (name == "CreditCardChargeModRq")
            {
                cts.Cancel();
            }
        };

        var outcome = harness.Execution.Execute(job, batch, cancellationToken: cts.Token);

        Assert.True(outcome.Halted);
        Assert.Contains("Stopped by user", outcome.HaltReason!, StringComparison.Ordinal);
        Assert.Equal(1, outcome.SucceededCount);
        Assert.NotEmpty(outcome.UnattemptedCandidateIds);
    }
}
