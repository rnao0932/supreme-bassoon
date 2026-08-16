namespace QbReclass.Core.Model;

/// <summary>
/// One line of a transaction, normalized across transaction types.
/// </summary>
/// <remarks>
/// <see cref="TxnLineId"/> is what a Mod request uses to say "keep this line". A qbXML Mod request
/// that omits an existing line <b>deletes</b> that line, which is the hazard the spec calls out in
/// section 10 under "Line preservation". Adapters therefore resubmit every line, changed or not.
/// </remarks>
public sealed record TransactionLineSnapshot
{
    /// <summary>QuickBooks line identifier. Null only for a line QuickBooks did not expose one for.</summary>
    public string? TxnLineId { get; init; }

    public LineKind Kind { get; init; } = LineKind.Expense;

    /// <summary>Position of this line within the transaction, 0-based. Used for stable ordering.</summary>
    public int Ordinal { get; init; }

    /// <summary>For expense lines, the posting account. For item lines, absent.</summary>
    public QbRef Account { get; init; } = QbRef.Empty;

    /// <summary>For item lines, the item. For expense lines, absent.</summary>
    public QbRef Item { get; init; } = QbRef.Empty;

    public decimal Amount { get; init; }
    public string? Memo { get; init; }
    public QbRef Class { get; init; } = QbRef.Empty;
    public QbRef Customer { get; init; } = QbRef.Empty;

    /// <summary>"Billable", "NotBillable" or "HasBeenBilled". Preserved verbatim on modification.</summary>
    public string? BillableStatus { get; init; }

    public QbRef SalesTaxCode { get; init; } = QbRef.Empty;

    /// <summary>Item-line quantity, preserved verbatim when the line is resubmitted.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Item-line unit cost, preserved verbatim when the line is resubmitted.</summary>
    public decimal? Cost { get; init; }

    /// <summary>True when this line has been billed to a customer; reclassifying it may affect invoicing.</summary>
    public bool HasBeenBilled =>
        string.Equals(BillableStatus, "HasBeenBilled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable line label for logs and the preview grid.</summary>
    public string Describe() =>
        $"line {Ordinal + 1} ({Kind}) {Account} {Amount:0.00}";
}

/// <summary>
/// A full transaction as read from QuickBooks, normalized across transaction types. This is the
/// unit compared before and after a write (spec section 14, steps 7-10).
/// </summary>
public sealed record TransactionSnapshot
{
    public required string TxnId { get; init; }

    /// <summary>
    /// QuickBooks version token. A Mod request must present the current value; QuickBooks rejects
    /// a stale one, which is the concurrency guarantee the utility relies on (spec section 3).
    /// </summary>
    public required string EditSequence { get; init; }

    public required TransactionType TxnType { get; init; }

    public DateOnly TxnDate { get; init; }
    public string? RefNumber { get; init; }
    public string? Memo { get; init; }

    /// <summary>Payee / vendor on the transaction.</summary>
    public QbRef Payee { get; init; } = QbRef.Empty;

    /// <summary>
    /// The bank or credit-card account the transaction posts against. Never modified by a
    /// reclassification; it is a protected field (spec section 10).
    /// </summary>
    public QbRef PostingAccount { get; init; } = QbRef.Empty;

    /// <summary>Transaction total as reported by QuickBooks.</summary>
    public decimal TotalAmount { get; init; }

    public ClearedStatus Cleared { get; init; } = ClearedStatus.Unknown;

    /// <summary>ISO currency code when the company file is multicurrency-enabled; otherwise null.</summary>
    public string? CurrencyCode { get; init; }

    /// <summary>Exchange rate when multicurrency is in play; otherwise null.</summary>
    public decimal? ExchangeRate { get; init; }

    /// <summary>True when line amounts include sales tax, which changes Mod semantics.</summary>
    public bool IsTaxIncluded { get; init; }

    /// <summary>QuickBooks timestamps, retained for the audit record.</summary>
    public DateTimeOffset? TimeCreated { get; init; }
    public DateTimeOffset? TimeModified { get; init; }

    public IReadOnlyList<TransactionLineSnapshot> Lines { get; init; } = [];

    /// <summary>Expense lines only, in document order.</summary>
    public IEnumerable<TransactionLineSnapshot> ExpenseLines =>
        Lines.Where(l => l.Kind == LineKind.Expense);

    public bool HasItemLines => Lines.Any(l => l.Kind is LineKind.Item or LineKind.ItemGroup);
    public bool HasItemGroupLines => Lines.Any(l => l.Kind == LineKind.ItemGroup);

    /// <summary>True when the transaction is reconciled or cleared and must be treated as high-risk.</summary>
    public bool IsReconciledOrCleared =>
        Cleared is ClearedStatus.Cleared or ClearedStatus.Reconciled;

    public TransactionLineSnapshot? FindLine(string txnLineId) =>
        Lines.FirstOrDefault(l => string.Equals(l.TxnLineId, txnLineId, StringComparison.Ordinal));

    public override string ToString() =>
        $"{TxnType} {TxnId} {TxnDate:yyyy-MM-dd} {Payee} {TotalAmount:0.00}";
}
