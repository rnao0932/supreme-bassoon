using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Core.Adapters;

/// <summary>
/// Query-only adapter for checks.
/// </summary>
/// <remarks>
/// <para>
/// Intuit's "QuickBooks Objects and Operations Accessible with the SDK" matrix lists Check as
/// queryable but exposes no <c>CheckMod</c> operation. The spec (sections 4 and 11) is emphatic
/// that this must not be worked around by deleting and recreating checks, and that unsupported
/// records are to be disclosed in the preview rather than handled by a materially different
/// accounting technique (FR-018).
/// </para>
/// <para>
/// This adapter therefore participates fully in query and preview - the user can see exactly which
/// checks match their rule - and refuses to build any write. If the Phase 0 spike establishes a
/// supported in-place path on the target QuickBooks edition, that path belongs here, behind its own
/// regression tests; until then <see cref="TypeSupport"/> stays
/// <see cref="ModificationSupport.NotSupported"/>.
/// </para>
/// </remarks>
public sealed class CheckAdapter : ITransactionAdapter
{
    /// <summary>Text shown against every check in the preview grid.</summary>
    public const string UnsupportedReason =
        "Intuit's SDK object matrix lists Check as queryable but provides no CheckMod operation. "
        + "This utility will not delete and recreate a check to simulate one. Checks are listed here "
        + "for review and must be corrected in QuickBooks by hand.";

    public TransactionType TxnType => TransactionType.Check;

    public string DisplayName => "Check";

    public ModificationSupport TypeSupport => ModificationSupport.NotSupported;

    public string? TypeUnsupportedReason => UnsupportedReason;

    public string? ModRequestName => null;

    public XElement BuildRangeQuery(DateOnly fromDate, DateOnly toDate, JobFilters filters, QueryPage page) =>
        QbXmlRequestBuilder.TransactionQuery(TxnType, fromDate, toDate, filters, page);

    public XElement BuildSingleQuery(string txnId) =>
        QbXmlRequestBuilder.TransactionQueryByTxnId(TxnType, txnId);

    public XElement BuildStopIterator(string iteratorId) =>
        QbXmlRequestBuilder.StopIterator(TxnType, iteratorId);

    public IReadOnlyList<TransactionSnapshot> ParseQueryResponse(QbResponse response) =>
        QbXmlResponseParser.ParseTransactions(response, TxnType);

    public RecordSupport EvaluateRecord(TransactionSnapshot snapshot) =>
        new(ModificationSupport.NotSupported, UnsupportedReason);

    public XElement BuildReclassification(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination) =>
        throw new NotSupportedException(UnsupportedReason);
}
