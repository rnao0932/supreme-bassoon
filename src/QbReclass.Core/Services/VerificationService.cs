using QbReclass.Core.Model;

namespace QbReclass.Core.Services;

/// <summary>
/// Read-back verification (spec section 12 "VerificationService", section 14 steps 8-10, FR-014).
/// </summary>
/// <remarks>
/// A qbXML status code of 0 says QuickBooks accepted the request, not that the resulting record is
/// what the utility intended. Every successful write is therefore read back and compared field by
/// field against the preflight snapshot. A mismatch halts the batch unconditionally
/// (spec section 15).
/// </remarks>
public sealed class VerificationService
{
    private readonly QueryService _query;

    public VerificationService(QueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    /// <summary>
    /// Re-queries a modified transaction and compares it with the state captured at preflight.
    /// </summary>
    /// <param name="before">Snapshot captured at preflight, immediately before the write.</param>
    /// <param name="targetLineIds">Lines the write was supposed to reclassify.</param>
    /// <param name="destinationListId">Account those lines should now point at.</param>
    public VerificationReport Verify(
        TransactionSnapshot before,
        IReadOnlyCollection<string> targetLineIds,
        string destinationListId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(targetLineIds);

        var after = _query.GetTransaction(before.TxnType, before.TxnId);

        if (after is null)
        {
            // The transaction vanished across a modification. This is the single worst outcome the
            // utility can observe and is reported as such rather than as a routine failure.
            return new VerificationReport
            {
                TxnId = before.TxnId,
                TargetLineReclassified = false,
                Differences =
                [
                    new ProtectedFieldDifference("Transaction", before.ToString(), "(no longer exists)"),
                ],
            };
        }

        return ProtectedFields.Compare(before, after, targetLineIds, destinationListId);
    }

    /// <summary>
    /// Verifies against a snapshot QuickBooks returned in the modification response, avoiding a
    /// second round trip. Falls back to a fresh read when the response carried no record.
    /// </summary>
    public VerificationReport VerifyAgainst(
        TransactionSnapshot before,
        TransactionSnapshot? afterFromModResponse,
        IReadOnlyCollection<string> targetLineIds,
        string destinationListId)
    {
        // The spec requires reading the transaction back (section 14 step 8), not merely trusting
        // the echo in the Mod response, so the fresh read is the default path. The echo is only
        // used when a caller explicitly opts in for a record it has already re-read.
        return afterFromModResponse is null
            ? Verify(before, targetLineIds, destinationListId)
            : ProtectedFields.Compare(before, afterFromModResponse, targetLineIds, destinationListId);
    }
}
