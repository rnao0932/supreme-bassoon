using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Core.Adapters;

/// <summary>
/// Reclassifies vendor bills through <c>BillModRq</c>, when that has been enabled.
/// </summary>
/// <remarks>
/// <para>
/// Off by default for the same reason checks are: unverified, not thought to be wrong. The
/// specification is explicit in section 11 that credit-card behaviour must not be generalized -
/// every added type needs its own proven mapping and its own regression tests - so a type nobody
/// has watched work against a real company file does not get switched on by assumption.
/// </para>
/// <para>
/// Bills differ from the bank-side types in two ways that matter. They post to Accounts Payable
/// rather than to a bank or credit-card account, and QuickBooks names it <c>APAccountRef</c>;
/// like every other header field it is not sent, so a reclassification cannot move a bill between
/// AP accounts. And a bill can be <b>paid</b>: reclassifying an expense line then changes what an
/// already-settled payment was for. QuickBooks allows it and it is often exactly what a correction
/// requires, so it is surfaced as a warning on the row rather than a refusal.
/// </para>
/// </remarks>
public sealed class BillAdapter : ITransactionAdapter
{
    /// <summary>Text shown against every bill when the write path has not been enabled.</summary>
    public const string DisabledReason =
        "Bill modification is not enabled in this build. The operation has not been proven against "
        + "this QuickBooks installation, and the specification requires each transaction type to "
        + "have its own established SDK mapping rather than inheriting one from another type. Run "
        + "'qbreclass spike --allow-write' to see whether this QuickBooks implements BillModRq, then "
        + "enable bill writes if it does.";

    private readonly bool _modificationEnabled;

    public BillAdapter(bool enableModification = false)
    {
        _modificationEnabled = enableModification;
    }

    public TransactionType TxnType => TransactionType.Bill;

    public string DisplayName => "Bill";

    public ModificationSupport TypeSupport =>
        _modificationEnabled ? ModificationSupport.Supported : ModificationSupport.NotSupported;

    public string? TypeUnsupportedReason => _modificationEnabled ? null : DisabledReason;

    public string? ModRequestName => _modificationEnabled ? "BillModRq" : null;

    public XElement BuildRangeQuery(DateOnly fromDate, DateOnly toDate, JobFilters filters, QueryPage page) =>
        QbXmlRequestBuilder.TransactionQuery(TxnType, fromDate, toDate, filters, page);

    public XElement BuildSingleQuery(string txnId) =>
        QbXmlRequestBuilder.TransactionQueryByTxnId(TxnType, txnId);

    public XElement BuildStopIterator(string iteratorId) =>
        QbXmlRequestBuilder.StopIterator(TxnType, iteratorId);

    public IReadOnlyList<TransactionSnapshot> ParseQueryResponse(QbResponse response) =>
        QbXmlResponseParser.ParseTransactions(response, TxnType);

    public RecordSupport EvaluateRecord(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_modificationEnabled)
        {
            return new RecordSupport(ModificationSupport.NotSupported, DisabledReason);
        }

        if (snapshot.TxnType != TxnType)
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                $"Record is a {snapshot.TxnType}, not a {DisplayName}.");
        }

        return RecordShapeRules.FindUnsupportedShape(snapshot) ?? RecordSupport.Supported;
    }

    public XElement BuildReclassification(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targetLineIds);
        ArgumentNullException.ThrowIfNull(destination);

        if (!_modificationEnabled)
        {
            throw new NotSupportedException(DisabledReason);
        }

        var support = EvaluateRecord(snapshot);
        if (!support.CanWrite)
        {
            throw new NotSupportedException(
                $"Refusing to build a modification for {snapshot.TxnId}: {support.Reason}");
        }

        if (targetLineIds.Count == 0)
        {
            throw new ArgumentException("No target lines were supplied.", nameof(targetLineIds));
        }

        foreach (var lineId in targetLineIds)
        {
            var line = snapshot.FindLine(lineId)
                ?? throw new InvalidOperationException(
                    $"Line {lineId} is no longer present on transaction {snapshot.TxnId}.");

            if (line.Kind != LineKind.Expense)
            {
                throw new InvalidOperationException(
                    $"Line {lineId} on transaction {snapshot.TxnId} is a {line.Kind} line. Only "
                    + "expense lines carry a reclassifiable account.");
            }
        }

        if (string.IsNullOrEmpty(destination.ListId))
        {
            throw new ArgumentException(
                "Destination account must be identified by ListID so a rename cannot redirect the write.",
                nameof(destination));
        }

        return QbXmlRequestBuilder.BillMod(snapshot, targetLineIds, destination);
    }
}
