using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Core.Adapters;

/// <summary>
/// Reclassifies credit card charges through <c>CreditCardChargeModRq</c>.
/// </summary>
/// <remarks>
/// This is the transaction type the spec identifies as the strongest initial candidate
/// (section 11): Intuit documents <c>CreditCardChargeMod</c> and states that the expense table can
/// be modified. The adapter's contribution to safety is line preservation - every expense and item
/// line on the transaction is resubmitted, because a qbXML Mod request deletes any existing line
/// it does not mention.
/// </remarks>
public sealed class CreditCardChargeAdapter : ITransactionAdapter
{
    public TransactionType TxnType => TransactionType.CreditCardCharge;

    public string DisplayName => "Credit Card Charge";

    public ModificationSupport TypeSupport => ModificationSupport.Supported;

    public string? TypeUnsupportedReason => null;

    public string? ModRequestName => "CreditCardChargeModRq";

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

        return QbXmlRequestBuilder.CreditCardChargeMod(snapshot, targetLineIds, destination);
    }
}
