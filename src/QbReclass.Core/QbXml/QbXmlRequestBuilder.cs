using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using QbReclass.Core.Model;

namespace QbReclass.Core.QbXml;

/// <summary>Iterator state for a paged query (spec section 16: use SDK iterators, not full scans).</summary>
public sealed record QueryPage(int MaxReturned = 200, string? IteratorId = null, bool IsFirstPage = true);

/// <summary>
/// Builds qbXML request envelopes.
/// </summary>
/// <remarks>
/// <para>
/// Child-element order inside a qbXML request is enforced by Intuit's schema: a correctly named
/// element in the wrong position is rejected outright. The orderings below follow the published
/// qbXML schema for the request types in scope, and the Phase 0 spike
/// (<c>qbreclass spike</c>) exercises each one against the real QuickBooks so that any
/// version-specific difference is discovered before the write path is trusted. Do not reorder
/// these element writes without re-running the spike.
/// </para>
/// <para>
/// The Mod builders deliberately omit header fields. In qbXML an omitted optional header element
/// leaves the stored value unchanged, so omitting them is the minimal mutation the spec asks for
/// in FR-013. Line elements behave the opposite way - an omitted existing line is <b>deleted</b> -
/// which is why every line is always resubmitted.
/// </para>
/// </remarks>
public static class QbXmlRequestBuilder
{
    /// <summary>Highest qbXML version this build knows how to construct.</summary>
    public const string PreferredVersion = "16.0";

    /// <summary>Lowest qbXML version that carries every element this utility relies on.</summary>
    public const string MinimumVersion = "8.0";

    /// <summary>
    /// Chooses the qbXML version to use for a session from the versions QuickBooks reports.
    /// Picks the highest supported version not above <see cref="PreferredVersion"/>.
    /// </summary>
    public static string NegotiateVersion(IEnumerable<string> supportedVersions)
    {
        ArgumentNullException.ThrowIfNull(supportedVersions);

        var preferred = ParseVersion(PreferredVersion);
        var minimum = ParseVersion(MinimumVersion);

        var best = supportedVersions
            .Select(v => (Text: v, Value: ParseVersion(v)))
            .Where(v => v.Value is not null && v.Value <= preferred && v.Value >= minimum)
            .OrderByDescending(v => v.Value)
            .Select(v => v.Text)
            .FirstOrDefault();

        return best ?? throw new QbXmlFormatException(
            "QuickBooks reported no qbXML version between " + MinimumVersion + " and " + PreferredVersion
            + ". Reported versions: " + string.Join(", ", supportedVersions));
    }

    private static Version? ParseVersion(string text) =>
        Version.TryParse(text.Trim(), out var v) ? v : null;

    /// <summary>Wraps one request element in a QBXML envelope at the given qbXML version.</summary>
    public static string Envelope(XElement request, string qbXmlVersion, bool stopOnError = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XProcessingInstruction("qbxml", $"version=\"{qbXmlVersion}\""),
            new XElement("QBXML",
                new XElement("QBXMLMsgsRq",
                    new XAttribute("onError", stopOnError ? "stopOnError" : "continueOnError"),
                    request)));

        return Serialize(doc);
    }

    /// <summary>
    /// Serializes a qbXML document with a UTF-8 declaration.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlWriter"/> takes its declared encoding from the underlying writer, not from
    /// <see cref="XmlWriterSettings.Encoding"/>. Writing to a plain <see cref="StringBuilder"/>
    /// therefore emits <c>encoding="utf-16"</c> regardless of the settings, which is not what the
    /// request processor is given. <see cref="Utf8StringWriter"/> reports UTF-8 so the declaration
    /// matches the bytes QuickBooks ends up parsing.
    /// </remarks>
    public static string Serialize(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Indent = false,
            OmitXmlDeclaration = false,
        };

        using var stringWriter = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            doc.Save(writer);
        }

        return stringWriter.ToString();
    }

    /// <summary>Queries the QuickBooks host: product, versions, file mode (spec section 3).</summary>
    public static XElement HostQuery(string requestId = "1") =>
        new("HostQueryRq", new XAttribute("requestID", requestId));

    /// <summary>Queries company identity so a job can be bound to one file (spec FR-001).</summary>
    public static XElement CompanyQuery(string requestId = "1") =>
        new("CompanyQueryRq", new XAttribute("requestID", requestId));

    /// <summary>Queries the chart of accounts, optionally including inactive accounts.</summary>
    public static XElement AccountQuery(bool includeInactive = true, string requestId = "1")
    {
        var rq = new XElement("AccountQueryRq", new XAttribute("requestID", requestId));
        rq.Add(new XElement("ActiveStatus", includeInactive ? "All" : "ActiveOnly"));
        return rq;
    }

    /// <summary>Queries a single account by ListID, used to re-check the destination at preflight.</summary>
    public static XElement AccountQueryByListId(string listId, string requestId = "1")
    {
        var rq = new XElement("AccountQueryRq", new XAttribute("requestID", requestId));
        rq.Add(new XElement("ListID", listId));
        return rq;
    }

    /// <summary>
    /// Queries transactions of one type over a date range, with line items included.
    /// </summary>
    /// <remarks>
    /// <c>AccountFilter</c> on a transaction query filters by the transaction's own posting
    /// account - the credit-card or bank account - <b>not</b> by the expense-line account. The
    /// job's source account is therefore matched line by line after the response is parsed; see
    /// <c>ReclassificationPlanner</c>. Pushing the date range and transaction type into the query
    /// is what keeps this off a full-file scan.
    /// </remarks>
    public static XElement TransactionQuery(
        TransactionType txnType,
        DateOnly fromDate,
        DateOnly toDate,
        JobFilters? filters = null,
        QueryPage? page = null,
        string requestId = "1")
    {
        var rqName = QueryRequestName(txnType);
        var rq = new XElement(rqName, new XAttribute("requestID", requestId));

        page ??= new QueryPage();
        if (page.IteratorId is not null)
        {
            rq.Add(new XAttribute("iterator", "Continue"));
            rq.Add(new XAttribute("iteratorID", page.IteratorId));
        }
        else if (page.IsFirstPage)
        {
            rq.Add(new XAttribute("iterator", "Start"));
        }

        // Schema order for the filter group: MaxReturned, ModifiedDateRangeFilter,
        // TxnDateRangeFilter, EntityFilter, AccountFilter, RefNumberFilter, CurrencyFilter.
        rq.Add(new XElement("MaxReturned", page.MaxReturned.ToString(CultureInfo.InvariantCulture)));

        rq.Add(new XElement(
            "TxnDateRangeFilter",
            new XElement("FromTxnDate", Date(fromDate)),
            new XElement("ToTxnDate", Date(toDate))));

        if (filters is { PostingAccountListIds.Count: > 0 })
        {
            var accountFilter = new XElement("AccountFilter");
            foreach (var listId in filters.PostingAccountListIds)
            {
                accountFilter.Add(new XElement("ListID", listId));
            }

            rq.Add(accountFilter);
        }

        // Must follow the filter group.
        rq.Add(new XElement("IncludeLineItems", "true"));

        return rq;
    }

    /// <summary>Re-reads one transaction by TxnID, for preflight and read-back verification.</summary>
    public static XElement TransactionQueryByTxnId(TransactionType txnType, string txnId, string requestId = "1")
    {
        var rq = new XElement(QueryRequestName(txnType), new XAttribute("requestID", requestId));
        rq.Add(new XElement("TxnID", txnId));
        rq.Add(new XElement("IncludeLineItems", "true"));
        return rq;
    }

    /// <summary>Closes an iterator the caller is abandoning, so QuickBooks releases it.</summary>
    public static XElement StopIterator(TransactionType txnType, string iteratorId, string requestId = "1")
    {
        var rq = new XElement(
            QueryRequestName(txnType),
            new XAttribute("requestID", requestId),
            new XAttribute("iterator", "Stop"),
            new XAttribute("iteratorID", iteratorId));
        return rq;
    }

    /// <summary>
    /// Builds a <c>CreditCardChargeModRq</c> that changes the account on the target lines and
    /// resubmits every other line unchanged so QuickBooks retains them.
    /// </summary>
    public static XElement CreditCardChargeMod(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination,
        string requestId = "1") =>
        TransactionMod("CreditCardChargeMod", snapshot, targetLineIds, destination, requestId);

    /// <summary>
    /// Builds a <c>CheckModRq</c> with the same guarantees as the credit-card equivalent.
    /// </summary>
    /// <remarks>
    /// A check's reference number is its check number and its posting account is the bank account;
    /// both are header fields, and both are left out of the request entirely, so a reclassification
    /// cannot renumber a check or move it between bank accounts. That is the same minimal-mutation
    /// rule the credit-card path uses, and it matters more here.
    /// </remarks>
    public static XElement CheckMod(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination,
        string requestId = "1") =>
        TransactionMod("CheckMod", snapshot, targetLineIds, destination, requestId);

    /// <summary>
    /// Builds a <c>BillModRq</c> with the same guarantees as the other types.
    /// </summary>
    /// <remarks>
    /// A bill posts to Accounts Payable through <c>APAccountRef</c>, and its vendor, date, due date
    /// and reference number are all header fields. None are sent, so a reclassification cannot move
    /// a bill between AP accounts, re-date it, or change what it is owed against.
    /// </remarks>
    public static XElement BillMod(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination,
        string requestId = "1") =>
        TransactionMod("BillMod", snapshot, targetLineIds, destination, requestId);

    /// <summary>
    /// Builds a modification request for any transaction type whose Mod element carries an expense
    /// table: TxnID, EditSequence, then every line resubmitted with the target lines redirected.
    /// </summary>
    /// <remarks>
    /// Shared deliberately. The dangerous part of this request is the same whatever the transaction
    /// type - omitting an existing line deletes it - so the code that gets it right is written once
    /// and every adapter inherits the same behaviour rather than reimplementing it.
    /// </remarks>
    private static XElement TransactionMod(
        string modElementName,
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination,
        string requestId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetLineIds);
        ArgumentNullException.ThrowIfNull(destination);

        var mod = new XElement(modElementName);

        // Required, in this order, before any other child.
        mod.Add(new XElement("TxnID", snapshot.TxnId));
        mod.Add(new XElement("EditSequence", snapshot.EditSequence));

        // Header fields are intentionally omitted: an omitted optional header element leaves the
        // stored value untouched, which is the minimal mutation FR-013 asks for.

        var targets = new HashSet<string>(targetLineIds, StringComparer.Ordinal);

        foreach (var line in snapshot.Lines.Where(l => l.Kind == LineKind.Expense).OrderBy(l => l.Ordinal))
        {
            var account = targets.Contains(line.TxnLineId ?? string.Empty) ? destination : line.Account;
            mod.Add(ExpenseLineMod(line, account));
        }

        foreach (var line in snapshot.Lines.Where(l => l.Kind == LineKind.Item).OrderBy(l => l.Ordinal))
        {
            mod.Add(ItemLineMod(line));
        }

        return new XElement(modElementName + "Rq", new XAttribute("requestID", requestId), mod);
    }

    /// <summary>
    /// One <c>ExpenseLineMod</c>. Every field read from QuickBooks is written back so that
    /// resubmitting a line to retain it does not quietly blank its memo, class or customer.
    /// </summary>
    private static XElement ExpenseLineMod(TransactionLineSnapshot line, QbRef account)
    {
        var el = new XElement("ExpenseLineMod");

        // Schema order: TxnLineID, AccountRef, Amount, Memo, CustomerRef, ClassRef,
        // SalesTaxCodeRef, BillableStatus.
        el.Add(new XElement("TxnLineID", line.TxnLineId));
        AddRef(el, "AccountRef", account);
        el.Add(new XElement("Amount", Money(line.Amount)));
        AddOptional(el, "Memo", line.Memo);
        AddRef(el, "CustomerRef", line.Customer);
        AddRef(el, "ClassRef", line.Class);
        AddRef(el, "SalesTaxCodeRef", line.SalesTaxCode);

        // HasBeenBilled is a state QuickBooks sets, not one a request may assert. Sending it back
        // is rejected, so the line is resubmitted without it and QuickBooks keeps the stored value.
        if (line.BillableStatus is not null && !line.HasBeenBilled)
        {
            el.Add(new XElement("BillableStatus", line.BillableStatus));
        }

        return el;
    }

    /// <summary>One <c>ItemLineMod</c>, resubmitted verbatim purely so QuickBooks retains the line.</summary>
    private static XElement ItemLineMod(TransactionLineSnapshot line)
    {
        var el = new XElement("ItemLineMod");

        // Schema order: TxnLineID, ItemRef, Desc, Quantity, UnitOfMeasure, Cost, Amount,
        // CustomerRef, ClassRef, SalesTaxCodeRef, BillableStatus.
        el.Add(new XElement("TxnLineID", line.TxnLineId));
        AddRef(el, "ItemRef", line.Item);
        AddOptional(el, "Desc", line.Memo);

        if (line.Quantity is { } quantity)
        {
            el.Add(new XElement("Quantity", quantity.ToString("0.#####", CultureInfo.InvariantCulture)));
        }

        if (line.Cost is { } cost)
        {
            el.Add(new XElement("Cost", Money(cost)));
        }

        el.Add(new XElement("Amount", Money(line.Amount)));
        AddRef(el, "CustomerRef", line.Customer);
        AddRef(el, "ClassRef", line.Class);
        AddRef(el, "SalesTaxCodeRef", line.SalesTaxCode);

        if (line.BillableStatus is not null && !line.HasBeenBilled)
        {
            el.Add(new XElement("BillableStatus", line.BillableStatus));
        }

        return el;
    }

    /// <summary>
    /// A TxnID that is well formed but cannot match any record. QuickBooks assigns identifiers of
    /// the form "1A2B-1234567890"; a zeroed one is syntactically ordinary and permanently absent.
    /// </summary>
    public const string NonexistentTxnId = "0-0";

    /// <summary>
    /// Builds a modification request that deliberately names a transaction that does not exist, to
    /// discover whether this QuickBooks recognizes the request type at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a read-only question asked with a write-shaped request. Because the TxnID can never
    /// match, QuickBooks has nothing to modify and nothing is changed whatever the answer; the
    /// information is entirely in which rejection comes back.
    /// </para>
    /// <para>
    /// A request type the edition does not implement is refused before the payload is considered,
    /// with status 500. A request type it does implement gets far enough to look for the record and
    /// fails to find it, with status 3120. Those two answers are what distinguishes "this operation
    /// does not exist" from "this operation exists and I did not give it a real record".
    /// </para>
    /// </remarks>
    public static XElement ModCapabilityProbe(string modRequestName, string requestId = "1")
    {
        ArgumentException.ThrowIfNullOrEmpty(modRequestName);

        // CheckModRq -> CheckMod
        var modElement = modRequestName.EndsWith("Rq", StringComparison.Ordinal)
            ? modRequestName[..^2]
            : modRequestName;

        return new XElement(
            modRequestName,
            new XAttribute("requestID", requestId),
            new XElement(
                modElement,
                new XElement("TxnID", NonexistentTxnId),
                new XElement("EditSequence", "0")));
    }

    /// <summary>
    /// Opens the QuickBooks window for a transaction so the user can inspect it by hand
    /// (spec section 8, "future enhancement"). Read-only; QuickBooks must be running with a UI.
    /// </summary>
    public static XElement TxnDisplay(TransactionType txnType, string txnId, string requestId = "1") =>
        new("TxnDisplayAddRq",
            new XAttribute("requestID", requestId),
            new XElement("TxnDisplayAdd",
                new XElement("TxnID", txnId),
                new XElement("TxnType", DisplayTxnTypeName(txnType))));

    /// <summary>qbXML query request element name for a transaction type.</summary>
    public static string QueryRequestName(TransactionType txnType) => txnType switch
    {
        TransactionType.CreditCardCharge => "CreditCardChargeQueryRq",
        TransactionType.CreditCardCredit => "CreditCardCreditQueryRq",
        TransactionType.Check => "CheckQueryRq",
        TransactionType.Bill => "BillQueryRq",
        _ => throw new ArgumentOutOfRangeException(
            nameof(txnType), txnType, "No qbXML query request is defined for this transaction type."),
    };

    /// <summary>qbXML return element name for a transaction type, e.g. "CreditCardChargeRet".</summary>
    public static string ReturnElementName(TransactionType txnType) => txnType switch
    {
        TransactionType.CreditCardCharge => "CreditCardChargeRet",
        TransactionType.CreditCardCredit => "CreditCardCreditRet",
        TransactionType.Check => "CheckRet",
        TransactionType.Bill => "BillRet",
        _ => throw new ArgumentOutOfRangeException(
            nameof(txnType), txnType, "No qbXML return element is defined for this transaction type."),
    };

    private static string DisplayTxnTypeName(TransactionType txnType) => txnType switch
    {
        TransactionType.CreditCardCharge => "CreditCardCharge",
        TransactionType.CreditCardCredit => "CreditCardCredit",
        TransactionType.Check => "Check",
        TransactionType.Bill => "Bill",
        _ => throw new ArgumentOutOfRangeException(nameof(txnType), txnType, "Unknown transaction type."),
    };

    private static void AddRef(XElement parent, string name, QbRef reference)
    {
        if (reference.IsEmpty)
        {
            return;
        }

        var el = new XElement(name);

        // ListID takes precedence: it survives a rename, FullName does not.
        if (!string.IsNullOrEmpty(reference.ListId))
        {
            el.Add(new XElement("ListID", reference.ListId));
        }
        else
        {
            el.Add(new XElement("FullName", reference.FullName));
        }

        parent.Add(el);
    }

    private static void AddOptional(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parent.Add(new XElement(name, value));
        }
    }

    internal static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
