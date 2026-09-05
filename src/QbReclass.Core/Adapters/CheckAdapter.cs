using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Core.Adapters;

/// <summary>
/// Reclassifies checks through <c>CheckModRq</c>, when that has been enabled.
/// </summary>
/// <remarks>
/// <para>
/// The write path is off by default. Not because it is thought to be wrong, but because it is
/// unverified: the specification asserts that Intuit's object matrix lists Check as queryable with
/// no <c>CheckMod</c> operation, and this project has never seen that claim tested against an
/// installation. Shipping it on by default would replace one unexamined assumption with another.
/// </para>
/// <para>
/// Enabling it is one flag, and the machinery behind it is the same machinery credit card charges
/// use: the same preflight, the same all-lines-resubmitted request, the same read-back verification
/// against the same protected fields. Nothing about checks gets a weaker guarantee.
/// </para>
/// <para>
/// Two header fields matter more here than elsewhere and are therefore not sent at all: a check's
/// <c>RefNumber</c> is its check number, and its <c>AccountRef</c> is the bank account it draws on.
/// Omitting them means a reclassification cannot renumber a check or move it between bank accounts.
/// </para>
/// <para>
/// What is still not on the table, enabled or not, is deleting and recreating a check to simulate a
/// modification. That is prohibited by specification sections 4, 5, 10 and 11 and is enforced by a
/// test, not merely by intent.
/// </para>
/// </remarks>
public sealed class CheckAdapter : ITransactionAdapter
{
    /// <summary>Text shown against every check when the write path has not been enabled.</summary>
    public const string DisabledReason =
        "Check modification is not enabled in this build. Intuit's published SDK object matrix lists "
        + "Check as queryable with no CheckMod operation, and that claim has not been tested against "
        + "this installation. Run 'qbreclass spike --allow-write' to find out whether this QuickBooks "
        + "implements CheckModRq, then enable check writes if it does. This utility will not delete "
        + "and recreate a check to work around the restriction.";

    private readonly bool _modificationEnabled;

    /// <param name="enableModification">
    /// Turns on the write path. Should follow evidence - the capability probe, and a reconciled
    /// multi-line check proven end to end - rather than preceding it.
    /// </param>
    public CheckAdapter(bool enableModification = false)
    {
        _modificationEnabled = enableModification;
    }

    public TransactionType TxnType => TransactionType.Check;

    public string DisplayName => "Check";

    public ModificationSupport TypeSupport =>
        _modificationEnabled ? ModificationSupport.Supported : ModificationSupport.NotSupported;

    public string? TypeUnsupportedReason => _modificationEnabled ? null : DisabledReason;

    public string? ModRequestName => _modificationEnabled ? "CheckModRq" : null;

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

        return QbXmlRequestBuilder.CheckMod(snapshot, targetLineIds, destination);
    }
}
