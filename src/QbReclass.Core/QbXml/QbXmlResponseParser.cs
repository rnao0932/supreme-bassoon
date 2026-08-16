using System.Globalization;
using System.Xml.Linq;
using QbReclass.Core.Model;

namespace QbReclass.Core.QbXml;

/// <summary>One parsed <c>*Rs</c> element: its status plus the payload element itself.</summary>
public sealed record QbResponse(string RequestName, QbStatus Status, XElement Element)
{
    /// <summary>Iterator handle QuickBooks returned, when the request used an iterator.</summary>
    public string? IteratorId => Element.Attribute("iteratorID")?.Value;

    /// <summary>Records left in the iterator, when reported.</summary>
    public int? IteratorRemainingCount =>
        int.TryParse(Element.Attribute("iteratorRemainingCount")?.Value, out var n) ? n : null;

    public int? RetCount =>
        int.TryParse(Element.Attribute("retCount")?.Value, out var n) ? n : null;
}

/// <summary>What the QuickBooks host reported about itself (spec section 3, Phase 0 spike).</summary>
public sealed record QbHostInfo
{
    public string? ProductName { get; init; }
    public string? MajorVersion { get; init; }
    public string? MinorVersion { get; init; }
    public string? Country { get; init; }

    /// <summary>"SingleUser" or "MultiUser".</summary>
    public string? FileMode { get; init; }

    public IReadOnlyList<string> SupportedQbXmlVersions { get; init; } = [];
}

/// <summary>Parses qbXML response envelopes into the utility's normalized models.</summary>
public static class QbXmlResponseParser
{
    /// <summary>
    /// Extracts the single response element from an envelope and its status.
    /// </summary>
    /// <exception cref="QbXmlFormatException">The envelope is not well-formed qbXML.</exception>
    public static QbResponse ParseEnvelope(string responseXml)
    {
        if (string.IsNullOrWhiteSpace(responseXml))
        {
            throw new QbXmlFormatException("QuickBooks returned an empty response.");
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(responseXml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new QbXmlFormatException("QuickBooks returned a malformed qbXML response.", ex);
        }

        var msgs = doc.Root?.Element("QBXMLMsgsRs")
            ?? throw new QbXmlFormatException("qbXML response has no QBXMLMsgsRs element.");

        var rs = msgs.Elements().FirstOrDefault()
            ?? throw new QbXmlFormatException("qbXML response contains no response element.");

        var code = int.TryParse(rs.Attribute("statusCode")?.Value, out var c) ? c : -1;
        var severity = rs.Attribute("statusSeverity")?.Value ?? "Unknown";
        var message = rs.Attribute("statusMessage")?.Value ?? string.Empty;

        return new QbResponse(rs.Name.LocalName, new QbStatus(code, severity, message), rs);
    }

    /// <summary>Parses a <c>HostQueryRs</c> payload.</summary>
    public static QbHostInfo ParseHost(QbResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var ret = response.Element.Element("HostRet");
        if (ret is null)
        {
            return new QbHostInfo();
        }

        return new QbHostInfo
        {
            ProductName = Text(ret, "ProductName"),
            MajorVersion = Text(ret, "MajorVersion"),
            MinorVersion = Text(ret, "MinorVersion"),
            Country = Text(ret, "Country"),
            FileMode = Text(ret, "QBFileMode"),
            SupportedQbXmlVersions = ret.Elements("SupportedQBXMLVersion")
                .Select(e => e.Value.Trim())
                .Where(v => v.Length > 0)
                .ToList(),
        };
    }

    /// <summary>
    /// Parses a <c>CompanyQueryRs</c> payload into a company identity.
    /// </summary>
    /// <param name="companyFileNameFromHost">
    /// Full path to the open company file. The Desktop SDK exposes this through the request
    /// processor's <c>GetCurrentCompanyFileName</c> rather than through qbXML, so the session
    /// supplies it. Used only when <c>CompanyRet</c> does not carry the file name itself.
    /// </param>
    public static CompanyIdentity ParseCompany(
        QbResponse response,
        QbHostInfo host,
        string? companyFileNameFromHost = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(host);

        var ret = response.Element.Element("CompanyRet")
            ?? throw new QbXmlFormatException("CompanyQueryRs contained no CompanyRet element.");

        var companyName = Text(ret, "CompanyName") ?? "(unnamed company)";
        var fileName = Text(ret, "CompanyFileName")
            ?? companyFileNameFromHost
            ?? throw new QbXmlFormatException(
                "QuickBooks did not report the open company file path. A job cannot be bound to an "
                + "unidentified company file.");

        return new CompanyIdentity
        {
            CompanyName = companyName,
            LegalCompanyName = Text(ret, "LegalCompanyName"),
            CompanyFileName = fileName,
            Ein = Text(ret, "EIN"),
            ProductName = host.ProductName,
            FileMode = host.FileMode,
            SupportedQbXmlVersion = host.SupportedQbXmlVersions.LastOrDefault(),
        };
    }

    /// <summary>Parses an <c>AccountQueryRs</c> payload.</summary>
    public static IReadOnlyList<QbAccount> ParseAccounts(QbResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Element.Elements("AccountRet")
            .Select(ret => new QbAccount
            {
                ListId = Text(ret, "ListID") ?? string.Empty,
                FullName = Text(ret, "FullName") ?? Text(ret, "Name") ?? string.Empty,
                AccountType = Text(ret, "AccountType") ?? "Unknown",
                AccountNumber = Text(ret, "AccountNumber"),
                IsActive = Bool(ret, "IsActive") ?? true,
                EditSequence = Text(ret, "EditSequence"),
                Description = Text(ret, "Desc"),
            })
            .Where(a => a.ListId.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Parses every transaction return element of the given type from a query response.
    /// </summary>
    public static IReadOnlyList<TransactionSnapshot> ParseTransactions(QbResponse response, TransactionType txnType)
    {
        ArgumentNullException.ThrowIfNull(response);

        var retName = QbXmlRequestBuilder.ReturnElementName(txnType);
        return response.Element.Elements(retName)
            .Select(ret => ParseTransaction(ret, txnType))
            .ToList();
    }

    /// <summary>Parses one transaction return element into a normalized snapshot.</summary>
    public static TransactionSnapshot ParseTransaction(XElement ret, TransactionType txnType)
    {
        ArgumentNullException.ThrowIfNull(ret);

        var lines = new List<TransactionLineSnapshot>();
        var ordinal = 0;

        foreach (var child in ret.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "ExpenseLineRet":
                    lines.Add(ParseExpenseLine(child, ordinal++));
                    break;

                case "ItemLineRet":
                    lines.Add(ParseItemLine(child, ordinal++, LineKind.Item));
                    break;

                case "ItemGroupLineRet":
                    // Group lines wrap their own ItemLineRet children. They are recorded as a
                    // single opaque line so the adapter can refuse the record rather than
                    // reconstruct something it does not fully model.
                    lines.Add(ParseItemLine(child, ordinal++, LineKind.ItemGroup));
                    break;
            }
        }

        return new TransactionSnapshot
        {
            TxnId = Text(ret, "TxnID") ?? throw new QbXmlFormatException($"{ret.Name.LocalName} had no TxnID."),
            EditSequence = Text(ret, "EditSequence")
                ?? throw new QbXmlFormatException($"{ret.Name.LocalName} had no EditSequence."),
            TxnType = txnType,
            TxnDate = Date(ret, "TxnDate") ?? default,
            RefNumber = Text(ret, "RefNumber"),
            Memo = Text(ret, "Memo"),
            Payee = Ref(ret, "PayeeEntityRef") ?? Ref(ret, "VendorRef") ?? QbRef.Empty,
            PostingAccount = Ref(ret, "AccountRef") ?? QbRef.Empty,
            TotalAmount = Money(ret, "Amount") ?? 0m,
            Cleared = ParseCleared(Text(ret, "ClearedStatus")),
            CurrencyCode = ret.Element("CurrencyRef")?.Element("FullName")?.Value,
            ExchangeRate = Money(ret, "ExchangeRate"),
            IsTaxIncluded = Bool(ret, "IsTaxIncluded") ?? false,
            TimeCreated = Timestamp(ret, "TimeCreated"),
            TimeModified = Timestamp(ret, "TimeModified"),
            Lines = lines,
        };
    }

    /// <summary>
    /// Maps the qbXML cleared-status token onto <see cref="ClearedStatus"/>.
    /// </summary>
    /// <remarks>
    /// Not every transaction return element in every qbXML version reports this. When it is
    /// absent the utility records <see cref="ClearedStatus.Unknown"/> and says so rather than
    /// assuming "not reconciled"; see docs/OPEN-QUESTIONS.md, question 4.
    /// </remarks>
    public static ClearedStatus ParseCleared(string? token) => token?.Trim() switch
    {
        null or "" => ClearedStatus.Unknown,
        "Cleared" => ClearedStatus.Cleared,
        "Reconciled" => ClearedStatus.Reconciled,
        "NotCleared" => ClearedStatus.NotCleared,
        "Pending" => ClearedStatus.NotCleared,
        _ => ClearedStatus.Unknown,
    };

    private static TransactionLineSnapshot ParseExpenseLine(XElement el, int ordinal) => new()
    {
        TxnLineId = Text(el, "TxnLineID"),
        Kind = LineKind.Expense,
        Ordinal = ordinal,
        Account = Ref(el, "AccountRef") ?? QbRef.Empty,
        Amount = Money(el, "Amount") ?? 0m,
        Memo = Text(el, "Memo"),
        Class = Ref(el, "ClassRef") ?? QbRef.Empty,
        Customer = Ref(el, "CustomerRef") ?? QbRef.Empty,
        SalesTaxCode = Ref(el, "SalesTaxCodeRef") ?? QbRef.Empty,
        BillableStatus = Text(el, "BillableStatus"),
    };

    private static TransactionLineSnapshot ParseItemLine(XElement el, int ordinal, LineKind kind) => new()
    {
        TxnLineId = Text(el, "TxnLineID"),
        Kind = kind,
        Ordinal = ordinal,
        Item = Ref(el, "ItemRef") ?? Ref(el, "ItemGroupRef") ?? QbRef.Empty,
        Amount = Money(el, "Amount") ?? Money(el, "TotalAmount") ?? 0m,
        Memo = Text(el, "Desc"),
        Class = Ref(el, "ClassRef") ?? QbRef.Empty,
        Customer = Ref(el, "CustomerRef") ?? QbRef.Empty,
        SalesTaxCode = Ref(el, "SalesTaxCodeRef") ?? QbRef.Empty,
        BillableStatus = Text(el, "BillableStatus"),
        Quantity = Money(el, "Quantity"),
        Cost = Money(el, "Cost"),
    };

    private static string? Text(XElement parent, string name)
    {
        var value = parent.Element(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? Bool(XElement parent, string name) =>
        Text(parent, name) is { } t ? string.Equals(t, "true", StringComparison.OrdinalIgnoreCase) : null;

    private static decimal? Money(XElement parent, string name) =>
        Text(parent, name) is { } t && decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private static DateOnly? Date(XElement parent, string name) =>
        Text(parent, name) is { } t && DateOnly.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static DateTimeOffset? Timestamp(XElement parent, string name) =>
        Text(parent, name) is { } t
        && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? v
            : null;

    private static QbRef? Ref(XElement parent, string name)
    {
        var el = parent.Element(name);
        if (el is null)
        {
            return null;
        }

        var listId = el.Element("ListID")?.Value;
        var fullName = el.Element("FullName")?.Value;
        return string.IsNullOrEmpty(listId) && string.IsNullOrEmpty(fullName) ? null : new QbRef(listId, fullName);
    }
}
