using System.Globalization;
using System.Xml.Linq;
using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Simulator;
using Xunit;

namespace QbReclass.Tests;

/// <summary>Envelope shape, version negotiation, status classification and request/response round trips.</summary>
public sealed class QbXmlTests
{
    [Fact]
    public void TheEnvelopeCarriesTheQbXmlProcessingInstructionAndStopsOnError()
    {
        var xml = QbXmlRequestBuilder.Envelope(QbXmlRequestBuilder.HostQuery(), "16.0");

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
        Assert.Contains("<?qbxml version=\"16.0\"?>", xml, StringComparison.Ordinal);
        Assert.Contains("onError=\"stopOnError\"", xml, StringComparison.Ordinal);
        Assert.Contains("<HostQueryRq requestID=\"1\" />", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "13.0", "14.0", "15.0", "16.0" }, "16.0")]
    [InlineData(new[] { "8.0", "9.0", "10.0" }, "10.0")]
    [InlineData(new[] { "16.0", "17.0", "18.0" }, "16.0")]
    [InlineData(new[] { "13.0" }, "13.0")]
    public void VersionNegotiationPicksTheHighestKnownSupportedVersion(string[] supported, string expected) =>
        Assert.Equal(expected, QbXmlRequestBuilder.NegotiateVersion(supported));

    [Fact]
    public void VersionNegotiationFailsLoudlyWhenNothingUsableIsOffered()
    {
        var error = Assert.Throws<QbXmlFormatException>(
            () => QbXmlRequestBuilder.NegotiateVersion(["2.0", "3.0"]));

        Assert.Contains("no qbXML version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, QbStatusCategory.Ok)]
    [InlineData(1, QbStatusCategory.NoMatchingRecords)]
    [InlineData(3200, QbStatusCategory.Conflict)]
    [InlineData(3120, QbStatusCategory.NotFound)]
    [InlineData(3140, QbStatusCategory.InvalidReference)]
    [InlineData(3260, QbStatusCategory.Permission)]
    [InlineData(500, QbStatusCategory.Unsupported)]
    [InlineData(9999, QbStatusCategory.Fatal)]
    public void StatusCodesMapToHandlingCategories(int code, QbStatusCategory expected) =>
        Assert.Equal(expected, QbStatus.Classify(code));

    /// <summary>An unfamiliar status must be fatal, not optimistically retried.</summary>
    [Fact]
    public void AnUnmappedStatusCodeIsTreatedAsFatal() =>
        Assert.Equal(QbStatusCategory.Fatal, QbStatus.Classify(4242));

    [Fact]
    public void ATransactionSurvivesTheRoundTripThroughQbXml()
    {
        var company = TestCompanies.Standard(out _, out _, out _);
        using var session = new SimulatedQbSession(company);

        var original = company.Transactions.Single(t => t.RefNumber == "1002");

        var request = QbXmlRequestBuilder.TransactionQueryByTxnId(
            TransactionType.CreditCardCharge, original.TxnId);
        var response = QbXmlResponseParser.ParseEnvelope(
            session.SendRequest(QbXmlRequestBuilder.Envelope(request, "16.0")));

        var parsed = Assert.Single(
            QbXmlResponseParser.ParseTransactions(response, TransactionType.CreditCardCharge));

        Assert.Equal(original.TxnId, parsed.TxnId);
        Assert.Equal(original.EditSequence, parsed.EditSequence);
        Assert.Equal(original.TxnDate, parsed.TxnDate);
        Assert.Equal(original.RefNumber, parsed.RefNumber);
        Assert.Equal(original.TotalAmount, parsed.TotalAmount);
        Assert.Equal(original.PostingAccount.ListId, parsed.PostingAccount.ListId);
        Assert.Equal(original.Lines.Count, parsed.Lines.Count);

        foreach (var before in original.Lines)
        {
            var after = parsed.FindLine(before.TxnLineId!);
            Assert.NotNull(after);
            Assert.Equal(before.Account.ListId, after!.Account.ListId);
            Assert.Equal(before.Amount, after.Amount);
            Assert.Equal(before.Memo, after.Memo);
        }

        // The hash the preflight check relies on is stable across the round trip.
        Assert.Equal(
            ProtectedFields.ComputeSnapshotHash(original),
            ProtectedFields.ComputeSnapshotHash(parsed));
    }

    /// <summary>
    /// Element order inside a Mod request is schema-enforced by QuickBooks, so it is asserted here
    /// rather than left to chance.
    /// </summary>
    [Fact]
    public void TheModificationRequestEmitsElementsInSchemaOrder()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        var txn = company.Transactions.Single(t => t.RefNumber == "1002");

        var mod = new CreditCardChargeAdapter()
            .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination))
            .Element("CreditCardChargeMod")!;

        var names = mod.Elements().Select(e => e.Name.LocalName).ToList();

        Assert.Equal("TxnID", names[0]);
        Assert.Equal("EditSequence", names[1]);
        Assert.All(names.Skip(2), n => Assert.Equal("ExpenseLineMod", n));

        var line = mod.Elements("ExpenseLineMod").First();
        Assert.Equal(
            new[] { "TxnLineID", "AccountRef", "Amount", "Memo", "BillableStatus" },
            line.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    /// <summary>Item lines follow expense lines, as the schema requires.</summary>
    [Fact]
    public void ItemLinesAreEmittedAfterExpenseLines()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        var txn = company.Transactions.Single(t => t.RefNumber == "1002");
        company.AddItemLine(txn.TxnId, "Widget", 15m, quantity: 3);

        var refreshed = company.Find(txn.TxnId)!;
        var mod = new CreditCardChargeAdapter()
            .BuildReclassification(refreshed, [refreshed.Lines[0].TxnLineId!], QbRef.FromAccount(destination))
            .Element("CreditCardChargeMod")!;

        var names = mod.Elements().Select(e => e.Name.LocalName).Skip(2).ToList();
        var lastExpense = names.LastIndexOf("ExpenseLineMod");
        var firstItem = names.IndexOf("ItemLineMod");

        Assert.True(firstItem > lastExpense, "Item line mods must follow every expense line mod.");
        Assert.Equal(2, names.Count(n => n == "ExpenseLineMod"));
        Assert.Equal(1, names.Count(n => n == "ItemLineMod"));
    }

    /// <summary>
    /// A billed line is resubmitted without BillableStatus: QuickBooks owns that state and rejects
    /// a request that asserts it.
    /// </summary>
    [Fact]
    public void ABilledLineIsResubmittedWithoutAssertingBillableStatus()
    {
        var company = TestCompanies.Standard(out _, out var destination, out _);
        var txn = company.Transactions.Single(t => t.RefNumber == "1001");

        company.Replace(txn with
        {
            Lines = txn.Lines.Select(l => l with { BillableStatus = "HasBeenBilled" }).ToList(),
        });

        var refreshed = company.Find(txn.TxnId)!;
        var mod = new CreditCardChargeAdapter()
            .BuildReclassification(refreshed, [refreshed.Lines[0].TxnLineId!], QbRef.FromAccount(destination))
            .Element("CreditCardChargeMod")!;

        Assert.Null(mod.Element("ExpenseLineMod")!.Element("BillableStatus"));
    }

    /// <summary>A destination without a ListID is refused: a name is not a stable identity.</summary>
    [Fact]
    public void ADestinationWithoutAListIdIsRefused()
    {
        var company = TestCompanies.Standard(out _, out _, out _);
        var txn = company.Transactions.Single(t => t.RefNumber == "1001");

        Assert.Throws<ArgumentException>(() => new CreditCardChargeAdapter()
            .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], new QbRef(null, "Automobile:Fuel")));
    }

    [Fact]
    public void AMalformedResponseIsReportedAsSuch()
    {
        Assert.Throws<QbXmlFormatException>(() => QbXmlResponseParser.ParseEnvelope("not xml at all"));
        Assert.Throws<QbXmlFormatException>(() => QbXmlResponseParser.ParseEnvelope("<QBXML/>"));
        Assert.Throws<QbXmlFormatException>(() => QbXmlResponseParser.ParseEnvelope(string.Empty));
    }

    [Fact]
    public void TheQueryPushesTheDateRangeAndLineItemsIntoTheRequest()
    {
        var request = QbXmlRequestBuilder.TransactionQuery(
            TransactionType.CreditCardCharge,
            new DateOnly(2024, 1, 1),
            new DateOnly(2024, 12, 31),
            new JobFilters(),
            new QueryPage(50));

        Assert.Equal("Start", request.Attribute("iterator")?.Value);
        Assert.Equal("50", request.Element("MaxReturned")?.Value);
        Assert.Equal("2024-01-01", request.Element("TxnDateRangeFilter")?.Element("FromTxnDate")?.Value);
        Assert.Equal("2024-12-31", request.Element("TxnDateRangeFilter")?.Element("ToTxnDate")?.Value);
        Assert.Equal("true", request.Element("IncludeLineItems")?.Value);

        var order = request.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal("MaxReturned", order[0]);
        Assert.Equal("IncludeLineItems", order[^1]);
    }

    /// <summary>
    /// qbXML amounts and dates are wire format, not display format: they must use a dot decimal
    /// separator and ISO dates whatever locale the workstation runs in. An accountant in a
    /// comma-decimal locale must not send QuickBooks "118,00".
    /// </summary>
    /// <remarks>
    /// The application deliberately does <b>not</b> set <c>InvariantGlobalization</c> — doing so
    /// leaves only the invariant culture, and WPF's DatePicker and DataGrid throw during layout
    /// when they cannot resolve a specific culture. Culture independence therefore has to come
    /// from the serialization code naming <see cref="CultureInfo.InvariantCulture"/>, which is
    /// what this pins down.
    /// </remarks>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void QbXmlUsesInvariantNumberAndDateFormatsWhateverTheLocale(string cultureName)
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            var company = TestCompanies.Standard(out _, out var destination, out _);
            var txn = company.Transactions.Single(t => t.RefNumber == "1002");

            var request = new CreditCardChargeAdapter()
                .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination));

            var xml = QbXmlRequestBuilder.Envelope(request, "16.0");

            Assert.Contains("<Amount>118.00</Amount>", xml, StringComparison.Ordinal);
            Assert.Contains("<Amount>44.25</Amount>", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("118,00", xml, StringComparison.Ordinal);

            var query = QbXmlRequestBuilder.Envelope(
                QbXmlRequestBuilder.TransactionQuery(
                    TransactionType.CreditCardCharge,
                    new DateOnly(2024, 3, 4),
                    new DateOnly(2024, 12, 31),
                    new JobFilters(),
                    new QueryPage(50)),
                "16.0");

            Assert.Contains("<FromTxnDate>2024-03-04</FromTxnDate>", query, StringComparison.Ordinal);
            Assert.Contains("<ToTxnDate>2024-12-31</ToTxnDate>", query, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// The preflight snapshot hash is compared across processes and across restarts, so it must not
    /// depend on the locale the workstation happens to run in.
    /// </summary>
    [Fact]
    public void TheSnapshotHashIsIdenticalAcrossLocales()
    {
        var original = CultureInfo.CurrentCulture;
        var hashes = new List<string>();

        try
        {
            foreach (var cultureName in new[] { "en-US", "de-DE", "fr-FR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(cultureName);

                var company = TestCompanies.Standard(out _, out _, out _);
                hashes.Add(ProtectedFields.ComputeSnapshotHash(
                    company.Transactions.Single(t => t.RefNumber == "1002")));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Single(hashes.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void SpecialCharactersInAMemoAreEscaped()
    {
        var company = new SimulatedCompany();
        var card = company.AddAccount("Visa", "CreditCard");
        var source = company.AddAccount("Office", "Expense");
        var destination = company.AddAccount("Fuel", "Expense");

        const string Nasty = "R&D <tag> \"quoted\" 'apostrophe'";
        var txn = company.AddTransaction(
            TransactionType.CreditCardCharge,
            card,
            new DateOnly(2024, 1, 5),
            "Vendor",
            [(source, 10m, Nasty)]);

        var request = new CreditCardChargeAdapter()
            .BuildReclassification(txn, [txn.Lines[0].TxnLineId!], QbRef.FromAccount(destination));

        var xml = QbXmlRequestBuilder.Envelope(request, "16.0");
        Assert.Contains("R&amp;D &lt;tag&gt;", xml, StringComparison.Ordinal);

        var reparsed = XDocument.Parse(xml);
        var memo = reparsed.Descendants("Memo").Single().Value;
        Assert.Equal(Nasty, memo);
    }
}
