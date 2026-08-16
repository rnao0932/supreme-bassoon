using System.Xml.Linq;
using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Session;

namespace QbReclass.Core.Services;

/// <summary>
/// Every read this utility performs against QuickBooks (spec section 12, "QueryService").
/// </summary>
/// <remarks>
/// This class never writes. It is safe to use in the application's default read-only mode
/// (FR-002), and the preview stage uses nothing else.
/// </remarks>
public sealed class QueryService
{
    private readonly IQbSession _session;
    private readonly AdapterRegistry _registry;
    private readonly Action<string>? _trace;

    public QueryService(IQbSession session, AdapterRegistry registry, Action<string>? trace = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _trace = trace;
    }

    /// <summary>Reads host product, file mode and supported qbXML versions.</summary>
    public QbHostInfo LoadHostInfo()
    {
        var response = Send(QbXmlRequestBuilder.HostQuery());
        RequireOk(response);
        return QbXmlResponseParser.ParseHost(response);
    }

    /// <summary>
    /// Reads the identity of the open company file. Called before any job is created and again
    /// before every batch, so a company switch is caught (spec section 10 "Company-file lock").
    /// </summary>
    public CompanyIdentity LoadCompanyIdentity(QbHostInfo host, string? companyFileNameFromHost = null)
    {
        var response = Send(QbXmlRequestBuilder.CompanyQuery());
        RequireOk(response);
        return QbXmlResponseParser.ParseCompany(response, host, companyFileNameFromHost);
    }

    /// <summary>Reads the chart of accounts.</summary>
    public IReadOnlyList<QbAccount> LoadAccounts(bool includeInactive = true)
    {
        var response = Send(QbXmlRequestBuilder.AccountQuery(includeInactive));

        if (response.Status.Code == QbStatus.NoMatch)
        {
            return [];
        }

        RequireOk(response);
        return QbXmlResponseParser.ParseAccounts(response);
    }

    /// <summary>
    /// Re-reads one account. Used at preflight to catch a destination account that was made
    /// inactive or deleted after the preview (spec section 15, section 18).
    /// </summary>
    public QbAccount? GetAccount(string listId)
    {
        ArgumentException.ThrowIfNullOrEmpty(listId);

        var response = Send(QbXmlRequestBuilder.AccountQueryByListId(listId));

        if (response.Status.Category is QbStatusCategory.NoMatchingRecords or QbStatusCategory.NotFound)
        {
            return null;
        }

        RequireOk(response);
        return QbXmlResponseParser.ParseAccounts(response).FirstOrDefault();
    }

    /// <summary>
    /// Re-reads one transaction by TxnID. This is the read behind both preflight and read-back
    /// verification (spec section 14, steps 2 and 8).
    /// </summary>
    public TransactionSnapshot? GetTransaction(TransactionType txnType, string txnId)
    {
        ArgumentException.ThrowIfNullOrEmpty(txnId);

        var adapter = _registry.Get(txnType);
        var response = Send(adapter.BuildSingleQuery(txnId));

        if (response.Status.Category is QbStatusCategory.NoMatchingRecords or QbStatusCategory.NotFound)
        {
            return null;
        }

        RequireOk(response);
        return adapter.ParseQueryResponse(response).FirstOrDefault();
    }

    /// <summary>
    /// Streams every transaction of one type in the date range, using the SDK iterator so the
    /// company file is not repeatedly rescanned (spec section 16).
    /// </summary>
    /// <remarks>
    /// Lazily evaluated. The caller controls how much is materialized, which is what lets the
    /// preview page or virtualize a large population.
    /// </remarks>
    public IEnumerable<TransactionSnapshot> QueryTransactions(
        TransactionType txnType,
        DateOnly fromDate,
        DateOnly toDate,
        JobFilters? filters = null,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var adapter = _registry.Get(txnType);
        filters ??= new JobFilters();

        string? iteratorId = null;
        var firstPage = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = new QueryPage(pageSize, iteratorId, firstPage);
            var response = Send(adapter.BuildRangeQuery(fromDate, toDate, filters, page));

            if (response.Status.Code == QbStatus.NoMatch)
            {
                yield break;
            }

            RequireOk(response);

            foreach (var snapshot in adapter.ParseQueryResponse(response))
            {
                yield return snapshot;
            }

            iteratorId = response.IteratorId;
            firstPage = false;

            var remaining = response.IteratorRemainingCount;
            if (iteratorId is null || remaining is null or <= 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Releases an iterator the caller abandoned. Best-effort: a failure here is logged and
    /// swallowed, because the iterator expires on its own when the session closes.
    /// </summary>
    public void StopIterator(TransactionType txnType, string iteratorId)
    {
        try
        {
            Send(_registry.Get(txnType).BuildStopIterator(iteratorId));
        }
        catch (Exception ex) when (ex is QbSessionException or QbXmlFormatException)
        {
            _trace?.Invoke($"Failed to stop iterator {iteratorId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the QuickBooks window for a transaction so the user can inspect it (spec section 8).
    /// Returns the status QuickBooks reported; this is a UI convenience, never a precondition.
    /// </summary>
    public QbStatus DisplayInQuickBooks(TransactionType txnType, string txnId)
    {
        var response = Send(QbXmlRequestBuilder.TxnDisplay(txnType, txnId));
        return response.Status;
    }

    /// <summary>Sends a request built elsewhere. Exposed for the Phase 0 spike's capability probes.</summary>
    public QbResponse Send(XElement request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var xml = QbXmlRequestBuilder.Envelope(request, _session.Info.QbXmlVersion);
        _trace?.Invoke($"--> {request.Name.LocalName}");

        var responseXml = _session.SendRequest(xml);
        var response = QbXmlResponseParser.ParseEnvelope(responseXml);

        _trace?.Invoke($"<-- {response.RequestName} {response.Status}");
        return response;
    }

    private static void RequireOk(QbResponse response)
    {
        if (response.Status.IsOk)
        {
            return;
        }

        throw new QbQueryException(response.RequestName, response.Status);
    }
}

/// <summary>Raised when a read QuickBooks was expected to satisfy came back with an error status.</summary>
public sealed class QbQueryException : Exception
{
    public QbQueryException(string requestName, QbStatus status)
        : base($"{requestName} failed: {status}")
    {
        RequestName = requestName;
        Status = status;
    }

    public string RequestName { get; }

    public QbStatus Status { get; }
}
