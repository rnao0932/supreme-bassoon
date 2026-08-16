using System.Globalization;


using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Session;

namespace QbReclass.Simulator;

/// <summary>
/// A qbXML-speaking test double for QuickBooks Desktop, backed by a <see cref="SimulatedCompany"/>.
/// </summary>
/// <remarks>
/// The behaviours it reproduces on purpose are the dangerous ones:
/// <list type="bullet">
/// <item>a modification request that omits an existing line <b>deletes</b> that line;</item>
/// <item>a stale <c>EditSequence</c> is rejected with status 3200;</item>
/// <item>a reference to a missing or inactive account is rejected with status 3140;</item>
/// <item>the session can drop mid-batch.</item>
/// </list>
/// Without those, a test suite would confirm only that the utility is internally consistent.
/// </remarks>
public sealed class SimulatedQbSession : IQbSession
{
    private readonly SimulatedCompany _company;
    private readonly Dictionary<string, IteratorState> _iterators = new(StringComparer.Ordinal);
    private int _requestCount;
    private bool _disposed;

    public SimulatedQbSession(SimulatedCompany company, bool readOnly = false)
    {
        _company = company ?? throw new ArgumentNullException(nameof(company));

        Info = new QbSessionInfo
        {
            Company = company.Identity,
            QbXmlVersion = QbXmlRequestBuilder.NegotiateVersion(company.Host.SupportedQbXmlVersions),
            SupportedQbXmlVersions = company.Host.SupportedQbXmlVersions,
            HostBitness = "64-bit",
            IsReadOnly = readOnly,
        };
    }

    public QbSessionInfo Info { get; }

    public bool IsOpen => !_disposed;

    /// <summary>Every request envelope this session received, for assertions about request shape.</summary>
    public List<string> SentRequests { get; } = [];

    /// <summary>Drops the session on the request with this 1-based index. Null disables.</summary>
    public int? DropSessionOnRequest { get; set; }

    /// <summary>
    /// Runs before each request is handled, receiving the request element name. Lets a test change
    /// the company between the utility's preflight read and its write.
    /// </summary>
    public Action<string, SimulatedCompany>? OnRequest { get; set; }

    /// <summary>Forces the next modification request to fail with this status. Cleared after use.</summary>
    public QbStatus? NextModStatusOverride { get; set; }

    public string SendRequest(string qbXmlRequest)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(qbXmlRequest);

        SentRequests.Add(qbXmlRequest);
        _requestCount++;

        if (DropSessionOnRequest == _requestCount)
        {
            _disposed = true;
            throw new QbSessionException(
                "The connection to QuickBooks was lost. QuickBooks may have been closed.");
        }

        var doc = XDocument.Parse(qbXmlRequest);
        var request = doc.Root?.Element("QBXMLMsgsRq")?.Elements().FirstOrDefault()
            ?? throw new QbSessionException("Request envelope contained no request element.");

        OnRequest?.Invoke(request.Name.LocalName, _company);

        var requestId = request.Attribute("requestID")?.Value ?? "1";

        return request.Name.LocalName switch
        {
            "HostQueryRq" => Respond("HostQueryRs", requestId, HostRet()),
            "CompanyQueryRq" => Respond("CompanyQueryRs", requestId, CompanyRet()),
            "AccountQueryRq" => HandleAccountQuery(request, requestId),
            "CreditCardChargeQueryRq" => HandleTransactionQuery(request, requestId, TransactionType.CreditCardCharge),
            "CheckQueryRq" => HandleTransactionQuery(request, requestId, TransactionType.Check),
            "CreditCardChargeModRq" => HandleCreditCardChargeMod(request, requestId),
            "CheckModRq" => Error(
                "CheckModRs",
                requestId,
                500,
                "The request has not been processed because this QuickBooks version does not support "
                + "modifying a check through the SDK."),
            "TxnDisplayAddRq" => Respond("TxnDisplayAddRs", requestId),
            _ => Error(
                request.Name.LocalName.Replace("Rq", "Rs", StringComparison.Ordinal),
                requestId,
                500,
                $"The request '{request.Name.LocalName}' is not supported."),
        };
    }

    private XElement HostRet()
    {
        var ret = new XElement("HostRet",
            new XElement("ProductName", _company.Host.ProductName),
            new XElement("MajorVersion", _company.Host.MajorVersion),
            new XElement("MinorVersion", _company.Host.MinorVersion),
            new XElement("Country", _company.Host.Country),
            new XElement("QBFileMode", _company.Host.FileMode));

        foreach (var version in _company.Host.SupportedQbXmlVersions)
        {
            ret.Add(new XElement("SupportedQBXMLVersion", version));
        }

        return ret;
    }

    private XElement CompanyRet()
    {
        var identity = _company.Identity;
        var ret = new XElement("CompanyRet",
            new XElement("CompanyName", identity.CompanyName),
            new XElement("LegalCompanyName", identity.LegalCompanyName ?? identity.CompanyName),
            new XElement("CompanyFileName", identity.CompanyFileName));

        if (identity.Ein is not null)
        {
            ret.Add(new XElement("EIN", identity.Ein));
        }

        return ret;
    }

    private string HandleAccountQuery(XElement request, string requestId)
    {
        var listIds = request.Elements("ListID").Select(e => e.Value).ToList();
        var includeInactive = request.Element("ActiveStatus")?.Value != "ActiveOnly";

        var matches = _company.Accounts
            .Where(a => listIds.Count == 0 || listIds.Contains(a.ListId, StringComparer.Ordinal))
            .Where(a => includeInactive || a.IsActive)
            .ToList();

        if (matches.Count == 0)
        {
            return Error("AccountQueryRs", requestId, 1, "No matching records were found.");
        }

        var rets = matches.Select(a =>
        {
            var el = new XElement("AccountRet",
                new XElement("ListID", a.ListId),
                new XElement("EditSequence", a.EditSequence),
                new XElement("Name", a.FullName.Split(':').Last()),
                new XElement("FullName", a.FullName),
                new XElement("IsActive", a.IsActive ? "true" : "false"),
                new XElement("AccountType", a.AccountType));

            if (a.AccountNumber is not null)
            {
                el.Add(new XElement("AccountNumber", a.AccountNumber));
            }

            return el;
        });

        return Respond("AccountQueryRs", requestId, rets.ToArray());
    }

    private string HandleTransactionQuery(XElement request, string requestId, TransactionType txnType)
    {
        var responseName = QbXmlRequestBuilder.QueryRequestName(txnType)
            .Replace("Rq", "Rs", StringComparison.Ordinal);

        var iteratorMode = request.Attribute("iterator")?.Value;
        var iteratorId = request.Attribute("iteratorID")?.Value;

        if (iteratorMode == "Stop")
        {
            if (iteratorId is not null)
            {
                _iterators.Remove(iteratorId);
            }

            return Respond(responseName, requestId);
        }

        List<TransactionSnapshot> matches;

        if (iteratorId is not null && _iterators.TryGetValue(iteratorId, out var state))
        {
            matches = state.Remaining;
        }
        else
        {
            matches = FilterTransactions(request, txnType);
            iteratorId = null;
        }

        if (matches.Count == 0 && iteratorId is null)
        {
            return Error(responseName, requestId, 1, "No matching records were found.");
        }

        var maxReturned = int.TryParse(request.Element("MaxReturned")?.Value, out var max)
            ? Math.Min(max, _company.PageSizeCap)
            : _company.PageSizeCap;

        var page = matches.Take(maxReturned).ToList();
        var rest = matches.Skip(maxReturned).ToList();

        var attributes = new List<XAttribute>
        {
            new("retCount", page.Count.ToString(CultureInfo.InvariantCulture)),
        };

        if (iteratorMode is "Start" or "Continue")
        {
            var handle = iteratorId ?? Guid.NewGuid().ToString("N");

            if (rest.Count > 0)
            {
                _iterators[handle] = new IteratorState(rest);
            }
            else
            {
                _iterators.Remove(handle);
            }

            attributes.Add(new XAttribute("iteratorID", handle));
            attributes.Add(new XAttribute(
                "iteratorRemainingCount", rest.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var rets = page.Select(t => TransactionRet(t, txnType)).ToArray();
        return Respond(responseName, requestId, rets, attributes);
    }

    private List<TransactionSnapshot> FilterTransactions(XElement request, TransactionType txnType)
    {
        IEnumerable<TransactionSnapshot> query = _company.Transactions.Where(t => t.TxnType == txnType);

        var txnIds = request.Elements("TxnID").Select(e => e.Value).ToList();
        if (txnIds.Count > 0)
        {
            return query.Where(t => txnIds.Contains(t.TxnId, StringComparer.Ordinal)).ToList();
        }

        var range = request.Element("TxnDateRangeFilter");
        if (range is not null)
        {
            if (DateOnly.TryParse(range.Element("FromTxnDate")?.Value, CultureInfo.InvariantCulture, out var from))
            {
                query = query.Where(t => t.TxnDate >= from);
            }

            if (DateOnly.TryParse(range.Element("ToTxnDate")?.Value, CultureInfo.InvariantCulture, out var to))
            {
                query = query.Where(t => t.TxnDate <= to);
            }
        }

        var accountFilter = request.Element("AccountFilter");
        if (accountFilter is not null)
        {
            var wanted = accountFilter.Elements("ListID").Select(e => e.Value).ToHashSet(StringComparer.Ordinal);
            if (wanted.Count > 0)
            {
                // Matches QuickBooks: on a transaction query this filters the transaction's own
                // posting account, not the accounts on its expense lines.
                query = query.Where(t => t.PostingAccount.ListId is not null && wanted.Contains(t.PostingAccount.ListId));
            }
        }

        return query.OrderBy(t => t.TxnDate).ThenBy(t => t.TxnId, StringComparer.Ordinal).ToList();
    }

    private string HandleCreditCardChargeMod(XElement request, string requestId)
    {
        const string ResponseName = "CreditCardChargeModRs";

        if (Info.IsReadOnly)
        {
            return Error(ResponseName, requestId, 3260, "Insufficient permission: the session is read-only.");
        }

        if (NextModStatusOverride is { } forced)
        {
            NextModStatusOverride = null;
            return Error(ResponseName, requestId, forced.Code, forced.Message);
        }

        var mod = request.Element("CreditCardChargeMod")
            ?? throw new QbSessionException("CreditCardChargeModRq contained no CreditCardChargeMod element.");

        var txnId = mod.Element("TxnID")?.Value ?? string.Empty;
        var editSequence = mod.Element("EditSequence")?.Value ?? string.Empty;

        var existing = _company.Find(txnId);
        if (existing is null || existing.TxnType != TransactionType.CreditCardCharge)
        {
            return Error(ResponseName, requestId, 3120, $"The transaction {txnId} could not be found.");
        }

        if (!string.Equals(existing.EditSequence, editSequence, StringComparison.Ordinal))
        {
            return Error(
                ResponseName,
                requestId,
                QbStatus.EditSequenceOutOfDate,
                "The provided edit sequence is out of date. The object was modified after it was read.");
        }

        var expenseMods = mod.Elements("ExpenseLineMod").ToList();
        var itemMods = mod.Elements("ItemLineMod").ToList();

        // QuickBooks leaves lines untouched only when the request mentions no lines at all. As soon
        // as it mentions any, the submitted set replaces the stored set - which is exactly how an
        // incautious modification silently deletes lines.
        List<TransactionLineSnapshot> lines;

        if (expenseMods.Count == 0 && itemMods.Count == 0)
        {
            lines = existing.Lines.ToList();
        }
        else
        {
            lines = BuildLinesFromMods(existing, expenseMods, itemMods, out var lineError);

            if (lineError is not null)
            {
                return Error(ResponseName, requestId, lineError.Value.Code, lineError.Value.Message);
            }
        }

        var updated = existing with
        {
            Lines = lines,
            TotalAmount = lines.Sum(l => l.Amount),
            EditSequence = _company.NextEditSequence(),
            TimeModified = DateTimeOffset.UtcNow,
        };

        _company.Replace(updated);

        return Respond(ResponseName, requestId, TransactionRet(updated, TransactionType.CreditCardCharge));
    }

    private List<TransactionLineSnapshot> BuildLinesFromMods(
        TransactionSnapshot existing,
        List<XElement> expenseMods,
        List<XElement> itemMods,
        out (int Code, string Message)? error)
    {
        error = null;
        var lines = new List<TransactionLineSnapshot>();
        var ordinal = 0;

        foreach (var el in expenseMods)
        {
            var lineId = el.Element("TxnLineID")?.Value;
            var previous = lineId is null ? null : existing.FindLine(lineId);

            if (previous is null)
            {
                error = (3120, $"The line item {lineId} could not be found on transaction {existing.TxnId}.");
                return lines;
            }

            var accountListId = el.Element("AccountRef")?.Element("ListID")?.Value;
            var account = previous.Account;

            if (accountListId is not null)
            {
                var target = _company.Accounts.FirstOrDefault(a => a.ListId == accountListId);

                if (target is null)
                {
                    error = (QbStatus.ReferenceNotFound, $"The account reference {accountListId} could not be found.");
                    return lines;
                }

                if (!target.IsActive)
                {
                    error = (QbStatus.ReferenceNotFound, $"The account '{target.FullName}' is not active.");
                    return lines;
                }

                account = QbRef.FromAccount(target);
            }

            lines.Add(previous with
            {
                Ordinal = ordinal++,
                Account = account,
                Amount = Decimal(el, "Amount") ?? previous.Amount,
                Memo = el.Element("Memo")?.Value,
                Class = Resolve(el, "ClassRef", previous.Class),
                Customer = Resolve(el, "CustomerRef", previous.Customer),
                SalesTaxCode = Resolve(el, "SalesTaxCodeRef", previous.SalesTaxCode),

                // QuickBooks keeps a HasBeenBilled line billed; the request may not assert it.
                BillableStatus = previous.HasBeenBilled
                    ? previous.BillableStatus
                    : el.Element("BillableStatus")?.Value,
            });
        }

        foreach (var el in itemMods)
        {
            var lineId = el.Element("TxnLineID")?.Value;
            var previous = lineId is null ? null : existing.FindLine(lineId);

            if (previous is null)
            {
                error = (3120, $"The line item {lineId} could not be found on transaction {existing.TxnId}.");
                return lines;
            }

            lines.Add(previous with
            {
                Ordinal = ordinal++,
                Amount = Decimal(el, "Amount") ?? previous.Amount,
                Memo = el.Element("Desc")?.Value,
                Quantity = Decimal(el, "Quantity"),
                Cost = Decimal(el, "Cost"),
                Class = Resolve(el, "ClassRef", previous.Class),
                Customer = Resolve(el, "CustomerRef", previous.Customer),
                SalesTaxCode = Resolve(el, "SalesTaxCodeRef", previous.SalesTaxCode),
                BillableStatus = previous.HasBeenBilled
                    ? previous.BillableStatus
                    : el.Element("BillableStatus")?.Value,
            });
        }

        return lines;
    }

    private XElement TransactionRet(TransactionSnapshot snapshot, TransactionType txnType)
    {
        var ret = new XElement(QbXmlRequestBuilder.ReturnElementName(txnType),
            new XElement("TxnID", snapshot.TxnId),
            new XElement("TimeCreated", (snapshot.TimeCreated ?? DateTimeOffset.UtcNow).ToString("O")),
            new XElement("TimeModified", (snapshot.TimeModified ?? DateTimeOffset.UtcNow).ToString("O")),
            new XElement("EditSequence", snapshot.EditSequence));

        AddRef(ret, "AccountRef", snapshot.PostingAccount);
        AddRef(ret, "PayeeEntityRef", snapshot.Payee);
        ret.Add(new XElement("TxnDate", snapshot.TxnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        AddIfPresent(ret, "RefNumber", snapshot.RefNumber);
        AddIfPresent(ret, "Memo", snapshot.Memo);
        ret.Add(new XElement("Amount", snapshot.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)));

        if (_company.ReportsClearedStatus && snapshot.Cleared != ClearedStatus.Unknown)
        {
            ret.Add(new XElement("ClearedStatus", snapshot.Cleared.ToString()));
        }

        if (snapshot.IsTaxIncluded)
        {
            ret.Add(new XElement("IsTaxIncluded", "true"));
        }

        if (snapshot.CurrencyCode is not null)
        {
            ret.Add(new XElement("CurrencyRef", new XElement("FullName", snapshot.CurrencyCode)));
        }

        if (snapshot.ExchangeRate is { } rate)
        {
            ret.Add(new XElement("ExchangeRate", rate.ToString("0.######", CultureInfo.InvariantCulture)));
        }

        foreach (var line in snapshot.Lines.OrderBy(l => l.Ordinal))
        {
            ret.Add(LineRet(line));
        }

        return ret;
    }

    private static XElement LineRet(TransactionLineSnapshot line)
    {
        var name = line.Kind switch
        {
            LineKind.Expense => "ExpenseLineRet",
            LineKind.Item => "ItemLineRet",
            _ => "ItemGroupLineRet",
        };

        var el = new XElement(name, new XElement("TxnLineID", line.TxnLineId));

        if (line.Kind == LineKind.Expense)
        {
            AddRef(el, "AccountRef", line.Account);
        }
        else
        {
            AddRef(el, line.Kind == LineKind.Item ? "ItemRef" : "ItemGroupRef", line.Item);
            AddIfPresent(el, "Desc", line.Memo);

            if (line.Quantity is { } q)
            {
                el.Add(new XElement("Quantity", q.ToString("0.#####", CultureInfo.InvariantCulture)));
            }

            if (line.Cost is { } c)
            {
                el.Add(new XElement("Cost", c.ToString("0.00", CultureInfo.InvariantCulture)));
            }
        }

        el.Add(new XElement("Amount", line.Amount.ToString("0.00", CultureInfo.InvariantCulture)));

        if (line.Kind == LineKind.Expense)
        {
            AddIfPresent(el, "Memo", line.Memo);
        }

        AddRef(el, "CustomerRef", line.Customer);
        AddRef(el, "ClassRef", line.Class);
        AddRef(el, "SalesTaxCodeRef", line.SalesTaxCode);
        AddIfPresent(el, "BillableStatus", line.BillableStatus);

        return el;
    }

    private static void AddRef(XElement parent, string name, QbRef reference)
    {
        if (reference.IsEmpty)
        {
            return;
        }

        var el = new XElement(name);

        if (!string.IsNullOrEmpty(reference.ListId))
        {
            el.Add(new XElement("ListID", reference.ListId));
        }

        if (!string.IsNullOrEmpty(reference.FullName))
        {
            el.Add(new XElement("FullName", reference.FullName));
        }

        parent.Add(el);
    }

    private static void AddIfPresent(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parent.Add(new XElement(name, value));
        }
    }

    private static decimal? Decimal(XElement parent, string name) =>
        decimal.TryParse(
            parent.Element(name)?.Value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    /// <summary>
    /// Reads a reference out of a Mod request the way QuickBooks does: a request may identify a
    /// list object by ListID alone, but the record - and every later query response - carries the
    /// resolved name too. Echoing back only what the request supplied would make a faithful
    /// resubmission look like the reference had changed.
    /// </summary>
    private static QbRef Resolve(XElement parent, string name, QbRef previous)
    {
        var el = parent.Element(name);
        if (el is null)
        {
            return QbRef.Empty;
        }

        var listId = el.Element("ListID")?.Value;
        var fullName = el.Element("FullName")?.Value;

        if (listId is not null && listId == previous.ListId)
        {
            return previous;
        }

        if (fullName is not null && fullName == previous.FullName)
        {
            return previous;
        }

        return new QbRef(listId, fullName);
    }

    private static string Respond(string responseName, string requestId, params XElement[] payload) =>
        Respond(responseName, requestId, payload, null);

    private static string Respond(
        string responseName,
        string requestId,
        XElement[] payload,
        IEnumerable<XAttribute>? extraAttributes)
    {
        var rs = new XElement(responseName,
            new XAttribute("requestID", requestId),
            new XAttribute("statusCode", "0"),
            new XAttribute("statusSeverity", "Info"),
            new XAttribute("statusMessage", "Status OK"));

        if (extraAttributes is not null)
        {
            rs.Add(extraAttributes);
        }

        rs.Add(payload);
        return Wrap(rs);
    }

    private static string Error(string responseName, string requestId, int code, string message)
    {
        var rs = new XElement(responseName,
            new XAttribute("requestID", requestId),
            new XAttribute("statusCode", code.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("statusSeverity", code == 1 ? "Warn" : "Error"),
            new XAttribute("statusMessage", message));

        return Wrap(rs);
    }

    private static string Wrap(XElement responseElement)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XProcessingInstruction("qbxml", "version=\"16.0\""),
            new XElement("QBXML", new XElement("QBXMLMsgsRs", responseElement)));

        return QbXmlRequestBuilder.Serialize(doc);
    }

    public void Dispose() => _disposed = true;

    private sealed record IteratorState(List<TransactionSnapshot> Remaining);
}
