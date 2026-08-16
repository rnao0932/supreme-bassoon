using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QbReclass.Core.Model;

/// <summary>A single protected attribute that changed when it should not have.</summary>
public sealed record ProtectedFieldDifference(string Field, string? Before, string? After)
{
    public override string ToString() => $"{Field}: '{Before ?? "(null)"}' -> '{After ?? "(null)"}'";
}

/// <summary>
/// Result of comparing a transaction before and after a write (spec section 14, steps 9-10).
/// </summary>
public sealed record VerificationReport
{
    public required string TxnId { get; init; }

    /// <summary>True when every target line now points at the destination account.</summary>
    public bool TargetLineReclassified { get; init; }

    /// <summary>Protected attributes that changed. Any entry here is a hard failure.</summary>
    public IReadOnlyList<ProtectedFieldDifference> Differences { get; init; } = [];

    /// <summary>EditSequence QuickBooks returned after the modification.</summary>
    public string? NewEditSequence { get; init; }

    public bool Passed => TargetLineReclassified && Differences.Count == 0;

    public string Describe() => Passed
        ? "verified"
        : TargetLineReclassified
            ? $"protected fields changed: {string.Join("; ", Differences)}"
            : $"target line was not reclassified{(Differences.Count > 0 ? $"; also: {string.Join("; ", Differences)}" : string.Empty)}";
}

/// <summary>
/// Defines which transaction attributes a reclassification must leave alone, and compares two
/// snapshots against that definition.
/// </summary>
/// <remarks>
/// Spec section 5 (Non-Goals) and section 10 forbid changing dates, amounts, payees, posting
/// accounts, reconciliation state, reference numbers and memos. This type is the single place that
/// rule is expressed, so verification and the preflight snapshot hash cannot drift apart.
/// </remarks>
public static class ProtectedFields
{
    /// <summary>
    /// Compares a post-write snapshot against the preflight snapshot.
    /// </summary>
    /// <param name="before">Snapshot captured during preflight, immediately before the write.</param>
    /// <param name="after">Snapshot re-queried from QuickBooks after the write.</param>
    /// <param name="targetLineIds">TxnLineIDs the write was supposed to reclassify.</param>
    /// <param name="destinationAccountListId">ListID the target lines should now point at.</param>
    public static VerificationReport Compare(
        TransactionSnapshot before,
        TransactionSnapshot after,
        IReadOnlyCollection<string> targetLineIds,
        string destinationAccountListId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(targetLineIds);

        var diffs = new List<ProtectedFieldDifference>();

        Compare(diffs, "TxnID", before.TxnId, after.TxnId);
        Compare(diffs, "TxnType", before.TxnType.ToString(), after.TxnType.ToString());
        Compare(diffs, "TxnDate", Fmt(before.TxnDate), Fmt(after.TxnDate));
        Compare(diffs, "TotalAmount", Fmt(before.TotalAmount), Fmt(after.TotalAmount));
        Compare(diffs, "Payee", before.Payee.Key, after.Payee.Key);
        Compare(diffs, "PostingAccount", before.PostingAccount.Key, after.PostingAccount.Key);
        Compare(diffs, "RefNumber", Norm(before.RefNumber), Norm(after.RefNumber));
        Compare(diffs, "Memo", Norm(before.Memo), Norm(after.Memo));
        Compare(diffs, "ClearedStatus", before.Cleared.ToString(), after.Cleared.ToString());
        Compare(diffs, "CurrencyCode", Norm(before.CurrencyCode), Norm(after.CurrencyCode));
        Compare(diffs, "ExchangeRate", Fmt(before.ExchangeRate), Fmt(after.ExchangeRate));
        Compare(diffs, "IsTaxIncluded", before.IsTaxIncluded.ToString(), after.IsTaxIncluded.ToString());
        Compare(diffs, "LineCount", Fmt(before.Lines.Count), Fmt(after.Lines.Count));

        var targets = new HashSet<string>(targetLineIds, StringComparer.Ordinal);
        var reclassified = targets.Count > 0;

        foreach (var beforeLine in before.Lines)
        {
            if (beforeLine.TxnLineId is null)
            {
                // QuickBooks exposed no identifier for this line, so it cannot be tracked across
                // the write. Adapters refuse such records up front; this is defence in depth.
                diffs.Add(new ProtectedFieldDifference(
                    $"Line[{beforeLine.Ordinal}].TxnLineID", "(none)", "(untrackable)"));
                continue;
            }

            var afterLine = after.FindLine(beforeLine.TxnLineId);
            if (afterLine is null)
            {
                diffs.Add(new ProtectedFieldDifference(
                    $"Line[{beforeLine.TxnLineId}]", beforeLine.Describe(), "(line no longer present)"));
                continue;
            }

            var label = $"Line[{beforeLine.TxnLineId}]";
            Compare(diffs, $"{label}.Kind", beforeLine.Kind.ToString(), afterLine.Kind.ToString());
            Compare(diffs, $"{label}.Amount", Fmt(beforeLine.Amount), Fmt(afterLine.Amount));
            Compare(diffs, $"{label}.Memo", Norm(beforeLine.Memo), Norm(afterLine.Memo));
            Compare(diffs, $"{label}.Class", beforeLine.Class.Key, afterLine.Class.Key);
            Compare(diffs, $"{label}.Customer", beforeLine.Customer.Key, afterLine.Customer.Key);
            Compare(diffs, $"{label}.BillableStatus", Norm(beforeLine.BillableStatus), Norm(afterLine.BillableStatus));
            Compare(diffs, $"{label}.Item", beforeLine.Item.Key, afterLine.Item.Key);
            Compare(diffs, $"{label}.Quantity", Fmt(beforeLine.Quantity), Fmt(afterLine.Quantity));

            if (targets.Contains(beforeLine.TxnLineId))
            {
                // The one attribute the write is allowed to change.
                if (!string.Equals(afterLine.Account.ListId, destinationAccountListId, StringComparison.Ordinal))
                {
                    reclassified = false;
                }
            }
            else
            {
                Compare(diffs, $"{label}.Account", beforeLine.Account.Key, afterLine.Account.Key);
            }
        }

        foreach (var afterLine in after.Lines)
        {
            if (afterLine.TxnLineId is not null && before.FindLine(afterLine.TxnLineId) is null)
            {
                diffs.Add(new ProtectedFieldDifference(
                    $"Line[{afterLine.TxnLineId}]", "(not present)", afterLine.Describe()));
            }
        }

        return new VerificationReport
        {
            TxnId = after.TxnId,
            TargetLineReclassified = reclassified,
            Differences = diffs,
            NewEditSequence = after.EditSequence,
        };
    }

    /// <summary>
    /// Stable hash over every protected attribute of a transaction. Stored on the candidate at
    /// preview and recomputed at preflight, so an edit that QuickBooks did not reflect in
    /// EditSequence is still caught (spec FR-012).
    /// </summary>
    public static string ComputeSnapshotHash(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder();
        sb.Append(snapshot.TxnId).Append(Sep)
          .Append(snapshot.TxnType).Append(Sep)
          .Append(Fmt(snapshot.TxnDate)).Append(Sep)
          .Append(Fmt(snapshot.TotalAmount)).Append(Sep)
          .Append(snapshot.Payee.Key).Append(Sep)
          .Append(snapshot.PostingAccount.Key).Append(Sep)
          .Append(Norm(snapshot.RefNumber)).Append(Sep)
          .Append(Norm(snapshot.Memo)).Append(Sep)
          .Append(snapshot.Cleared).Append(Sep)
          .Append(Norm(snapshot.CurrencyCode)).Append(Sep)
          .Append(Fmt(snapshot.ExchangeRate)).Append(Sep)
          .Append(snapshot.IsTaxIncluded).Append('');

        foreach (var line in snapshot.Lines.OrderBy(l => l.Ordinal))
        {
            sb.Append(line.TxnLineId ?? "(none)").Append(Sep)
              .Append(line.Kind).Append(Sep)
              .Append(line.Account.Key).Append(Sep)
              .Append(line.Item.Key).Append(Sep)
              .Append(Fmt(line.Amount)).Append(Sep)
              .Append(Norm(line.Memo)).Append(Sep)
              .Append(line.Class.Key).Append(Sep)
              .Append(line.Customer.Key).Append(Sep)
              .Append(Norm(line.BillableStatus)).Append(Sep)
              .Append(Fmt(line.Quantity)).Append('');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Field separator used when building the snapshot hash material.</summary>
    private const char Sep = '\u001F';

    private static void Compare(List<ProtectedFieldDifference> into, string field, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            into.Add(new ProtectedFieldDifference(field, before, after));
        }
    }

    private static string Norm(string? value) => value?.Trim() ?? string.Empty;

    private static string Fmt(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Fmt(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    private static string Fmt(decimal? value) =>
        value is null ? string.Empty : Fmt(value.Value);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);
}
