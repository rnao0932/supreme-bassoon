using System.Xml.Linq;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Core.Adapters;

/// <summary>Whether one specific record can be reclassified, and why not when it cannot.</summary>
public sealed record RecordSupport(ModificationSupport Support, string? Reason = null)
{
    public static readonly RecordSupport Supported = new(ModificationSupport.Supported);

    public bool CanWrite => Support == ModificationSupport.Supported;
}

/// <summary>
/// One adapter per QuickBooks transaction type (spec section 12, "TransactionAdapter(s)").
/// </summary>
/// <remarks>
/// The spec is explicit that credit-card behaviour must not be generalized to other types
/// (section 11): every type needs its own query/modification mapping and its own regression tests.
/// An adapter is the only place that mapping lives, and an adapter that cannot prove a supported
/// in-place modification reports <see cref="ModificationSupport.NotSupported"/> rather than
/// improvising one.
/// </remarks>
public interface ITransactionAdapter
{
    TransactionType TxnType { get; }

    /// <summary>Human-readable type name for the preview grid, e.g. "Credit Card Charge".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether Intuit documents an in-place modification for this type at all. Evaluated once per
    /// type, before any record is looked at.
    /// </summary>
    ModificationSupport TypeSupport { get; }

    /// <summary>Explanation shown for every record of an unsupported type. Null when supported.</summary>
    string? TypeUnsupportedReason { get; }

    /// <summary>qbXML request name used to modify this type, or null when there is none.</summary>
    string? ModRequestName { get; }

    XElement BuildRangeQuery(DateOnly fromDate, DateOnly toDate, JobFilters filters, QueryPage page);

    XElement BuildSingleQuery(string txnId);

    XElement BuildStopIterator(string iteratorId);

    IReadOnlyList<TransactionSnapshot> ParseQueryResponse(QbResponse response);

    /// <summary>
    /// Decides whether this particular record can be safely reclassified in place. Catches record
    /// shapes the adapter cannot faithfully reconstruct even though the type is modifiable.
    /// </summary>
    RecordSupport EvaluateRecord(TransactionSnapshot snapshot);

    /// <summary>
    /// Builds the modification request that points <paramref name="targetLineIds"/> at
    /// <paramref name="destination"/> and preserves every other line.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// This type has no supported in-place modification. Callers must check
    /// <see cref="TypeSupport"/> and <see cref="EvaluateRecord"/> first; reaching here is a bug.
    /// </exception>
    XElement BuildReclassification(
        TransactionSnapshot snapshot,
        IReadOnlyCollection<string> targetLineIds,
        QbRef destination);
}

/// <summary>
/// Shared record-shape checks. A record that trips any of these is shown in preview and never
/// written, which is what FR-018 requires instead of a silent fallback.
/// </summary>
internal static class RecordShapeRules
{
    /// <summary>
    /// Evaluates constructs that no adapter in this version can round-trip. Returns null when the
    /// record is safe to modify.
    /// </summary>
    internal static RecordSupport? FindUnsupportedShape(TransactionSnapshot snapshot)
    {
        if (snapshot.HasItemGroupLines)
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                "Transaction contains an item group line. Resubmitting a group line to retain it is "
                + "not modelled by this version, and omitting it would delete the group.");
        }

        if (snapshot.ExchangeRate is not null || !string.IsNullOrEmpty(snapshot.CurrencyCode))
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                "Transaction is in a foreign currency. Multicurrency modification is out of scope "
                + "for version 1 because the home-currency amounts are recalculated on save.");
        }

        if (snapshot.IsTaxIncluded)
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                "Transaction line amounts include sales tax. Tax-inclusive modification is out of "
                + "scope for version 1.");
        }

        var untrackable = snapshot.Lines.FirstOrDefault(l => string.IsNullOrEmpty(l.TxnLineId));
        if (untrackable is not null)
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                $"QuickBooks did not report a TxnLineID for {untrackable.Describe()}. Without a line "
                + "identifier the line cannot be retained on modification.");
        }

        var duplicate = snapshot.Lines
            .Where(l => l.TxnLineId is not null)
            .GroupBy(l => l.TxnLineId!, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                $"TxnLineID '{duplicate.Key}' appears on more than one line, so lines cannot be "
                + "matched reliably before and after a write.");
        }

        if (!snapshot.ExpenseLines.Any())
        {
            return new RecordSupport(
                ModificationSupport.UnsupportedRecordShape,
                "Transaction has no expense-account lines to reclassify.");
        }

        return null;
    }
}
