using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Services;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>
/// The capability probe that settles whether checks can be modified in place, rather than assuming
/// it from Intuit's published object matrix (spec sections 4 and 11, open question 5).
/// </summary>
public sealed class CheckFeasibilityTests
{
    private static ProbeReport RunProbe(SimulatedCompany company, bool readOnly)
    {
        using var session = new SimulatedQbSession(company, readOnly);
        var registry = AdapterRegistry.Default;
        var query = new QueryService(session, registry);

        return new FeasibilityProbe(session, query, registry).Run(new ProbeOptions
        {
            FromDate = new DateOnly(2020, 1, 1),
            ToDate = new DateOnly(2030, 12, 31),
        });
    }

    /// <summary>The probe must not be able to touch a record, whatever the answer turns out to be.</summary>
    [Fact]
    public void TheProbeNamesATransactionThatCannotExist()
    {
        var request = QbXmlRequestBuilder.ModCapabilityProbe("CheckModRq");
        var mod = request.Element("CheckMod")!;

        Assert.Equal("CheckModRq", request.Name.LocalName);
        Assert.Equal(QbXmlRequestBuilder.NonexistentTxnId, mod.Element("TxnID")!.Value);

        // No line elements at all: there is nothing here that could modify a record even if the
        // TxnID were somehow matched.
        Assert.Empty(mod.Elements("ExpenseLineMod"));
        Assert.Empty(mod.Elements("ItemLineMod"));
    }

    /// <summary>An edition that refuses CheckModRq confirms the shipping assumption.</summary>
    [Fact]
    public void WhenCheckModIsUnsupported_TheProbeConfirmsChecksStayPreviewOnly()
    {
        var company = TestCompanies.Standard(out _, out _, out _);
        company.SupportsCheckMod = false;

        var report = RunProbe(company, readOnly: false);
        var checks = Assert.Single(report.Findings, f => f.Area == "Checks");

        Assert.Contains("does not implement CheckModRq", checks.Message, StringComparison.Ordinal);
        Assert.Equal(ProbeSeverity.Info, checks.Severity);
        Assert.False(report.HasBlockers);
    }

    /// <summary>
    /// The answer that would change the project. An edition that reports "no such transaction"
    /// rather than "no such request" has implemented the operation, and the report must say so
    /// loudly rather than leaving the assumption standing.
    /// </summary>
    [Fact]
    public void WhenCheckModIsImplemented_TheProbeSaysTheScopeShouldBeRevisited()
    {
        var company = TestCompanies.Standard(out _, out _, out _);
        company.SupportsCheckMod = true;

        var report = RunProbe(company, readOnly: false);
        var checks = Assert.Single(report.Findings, f => f.Area == "Checks");

        Assert.Equal(ProbeSeverity.Warning, checks.Severity);
        Assert.Contains("appears to IMPLEMENT CheckModRq", checks.Message, StringComparison.Ordinal);
        Assert.Contains("scope of version 1 should be revisited", checks.Message, StringComparison.Ordinal);

        // It must not quietly start writing checks on the strength of a probe.
        Assert.Contains("regression tests", checks.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModificationSupport.NotSupported, new CheckAdapter().TypeSupport);
    }

    /// <summary>
    /// Credit card charges are probed as a control, so "supported" has a known shape on this
    /// edition to compare the check answer against.
    /// </summary>
    [Fact]
    public void TheControlProbeRecordsWhatSupportedLooksLike()
    {
        var company = TestCompanies.Standard(out _, out _, out _);

        var report = RunProbe(company, readOnly: false);
        var control = Assert.Single(
            report.Findings, f => f.Area == "Mod capability: Credit Card Charge (control)");

        // 3120: the request type exists and got as far as looking for the record.
        Assert.Contains("3120", control.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read-only session refuses modification requests without exception, and the probe is one.
    /// It must be skipped and explained rather than allowed through as a special case.
    /// </summary>
    [Fact]
    public void AReadOnlySessionSkipsTheProbeAndSaysHowToRunIt()
    {
        var company = TestCompanies.Standard(out _, out _, out _);

        var report = RunProbe(company, readOnly: true);
        var skipped = Assert.Single(report.Findings, f => f.Area == "Mod capability");

        Assert.Contains("read-only", skipped.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--allow-write", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Area == "Checks");
    }

    /// <summary>Whatever the probe reports, no check in the company was altered.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProbeChangesNothing(bool supportsCheckMod)
    {
        var company = TestCompanies.Standard(out _, out _, out _);
        company.SupportsCheckMod = supportsCheckMod;

        var before = company.Transactions
            .Select(t => (t.TxnId, t.EditSequence, Accounts: t.Lines.Select(l => l.Account.ListId).ToList()))
            .ToList();

        RunProbe(company, readOnly: false);

        foreach (var (txnId, editSequence, accounts) in before)
        {
            var after = company.Find(txnId)!;
            Assert.Equal(editSequence, after.EditSequence);
            Assert.Equal(accounts, after.Lines.Select(l => l.Account.ListId).ToList());
        }
    }
}
