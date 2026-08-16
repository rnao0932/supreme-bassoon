namespace QbReclass.Core.Model;

/// <summary>A QuickBooks chart-of-accounts entry as returned by <c>AccountQueryRs</c>.</summary>
public sealed record QbAccount
{
    public required string ListId { get; init; }

    /// <summary>Colon-delimited full name, e.g. "Automobile:Fuel".</summary>
    public required string FullName { get; init; }

    /// <summary>QuickBooks account type string, e.g. "Expense", "CostOfGoodsSold", "CreditCard".</summary>
    public required string AccountType { get; init; }

    public string? AccountNumber { get; init; }
    public bool IsActive { get; init; } = true;
    public string? EditSequence { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Account types a reclassification may target. QuickBooks will reject an expense line pointed
    /// at a balance-sheet account, so the utility rejects it first with a clearer message (FR-005).
    /// </summary>
    public static readonly IReadOnlySet<string> ExpenseSideAccountTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Expense",
        "OtherExpense",
        "CostOfGoodsSold",
        "OtherCurrentAsset",
        "FixedAsset",
        "OtherAsset",
    };

    /// <summary>True when this account is a legal target for an expense-line reclassification.</summary>
    public bool IsExpenseSide => ExpenseSideAccountTypes.Contains(AccountType);

    public override string ToString() => FullName;
}

/// <summary>A reference to a QuickBooks list object by ListID and/or FullName.</summary>
public sealed record QbRef(string? ListId, string? FullName)
{
    public bool IsEmpty => string.IsNullOrEmpty(ListId) && string.IsNullOrEmpty(FullName);

    public static QbRef Empty { get; } = new(null, null);

    public static QbRef FromAccount(QbAccount account) => new(account.ListId, account.FullName);

    /// <summary>
    /// Identity of the referenced object, for comparison. ListID is preferred because it is what
    /// QuickBooks keys on: renaming an account changes its FullName but not what a line points at,
    /// and a rename must not be reported as a protected field having changed.
    /// </summary>
    public string Key => ListId ?? FullName ?? string.Empty;

    public override string ToString() => FullName ?? ListId ?? "(none)";
}
