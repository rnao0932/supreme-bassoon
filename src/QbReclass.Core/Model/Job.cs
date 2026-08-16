namespace QbReclass.Core.Model;

/// <summary>
/// Optional narrowing filters applied on top of the mandatory date range and source account
/// (spec FR-007). Filters QuickBooks can apply server-side are noted; the rest are applied locally
/// after the query returns, because the Desktop SDK has no filter for them.
/// </summary>
public sealed record JobFilters
{
    /// <summary>Payee name substring, case-insensitive. Applied locally.</summary>
    public string? PayeeContains { get; init; }

    /// <summary>Memo substring matched against transaction memo and line memo. Applied locally.</summary>
    public string? MemoContains { get; init; }

    /// <summary>Reference / check number substring. Applied locally.</summary>
    public string? RefNumberContains { get; init; }

    /// <summary>Inclusive lower bound on the absolute line amount. Applied locally.</summary>
    public decimal? MinAmount { get; init; }

    /// <summary>Inclusive upper bound on the absolute line amount. Applied locally.</summary>
    public decimal? MaxAmount { get; init; }

    /// <summary>Restrict to lines carrying this class. Applied locally.</summary>
    public string? ClassFullName { get; init; }

    /// <summary>
    /// Restrict to transactions posting against these bank/credit-card accounts. Pushed into the
    /// query as <c>AccountFilter</c>, which on a transaction query filters the posting account.
    /// </summary>
    public IReadOnlyList<string> PostingAccountListIds { get; init; } = [];

    /// <summary>When set, keep only transactions whose cleared status is in this set. Applied locally.</summary>
    public IReadOnlyList<ClearedStatus> ClearedStatuses { get; init; } = [];

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(PayeeContains)
        && string.IsNullOrWhiteSpace(MemoContains)
        && string.IsNullOrWhiteSpace(RefNumberContains)
        && MinAmount is null
        && MaxAmount is null
        && string.IsNullOrWhiteSpace(ClassFullName)
        && PostingAccountListIds.Count == 0
        && ClearedStatuses.Count == 0;
}

/// <summary>
/// A reclassification job: one source account, one destination account, one date range, one
/// company (spec section 13). A job is immutable once previewed except for its status.
/// </summary>
public sealed record Job
{
    public required string JobId { get; init; }

    /// <summary>Company this job is permanently bound to. Execution against any other is refused.</summary>
    public required CompanyIdentity Company { get; init; }

    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }

    /// <summary>Account the expense lines currently point at (spec FR-004).</summary>
    public required QbAccount SourceAccount { get; init; }

    /// <summary>Account the approved lines should point at (spec FR-005).</summary>
    public required QbAccount DestinationAccount { get; init; }

    /// <summary>Transaction types included in the query (spec FR-006).</summary>
    public required IReadOnlyList<TransactionType> TransactionTypes { get; init; }

    public JobFilters Filters { get; init; } = new();

    public JobStatus Status { get; init; } = JobStatus.Draft;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the user confirmed a current QuickBooks backup exists. Null blocks write mode
    /// (spec section 6 step 2, section 10 "Backup confirmation").
    /// </summary>
    public DateTimeOffset? BackupConfirmedAt { get; init; }

    /// <summary>Default batch size; deliberately conservative (spec FR-010).</summary>
    public int BatchSize { get; init; } = 25;

    public StopPolicy StopPolicy { get; init; } = StopPolicy.StopOnFirstFailure;

    /// <summary>Human-readable rule summary shown on the approval screen (spec section 9).</summary>
    public string RuleDescription =>
        $"{SourceAccount.FullName} -> {DestinationAccount.FullName}, "
        + $"{FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}, "
        + $"types: {string.Join(", ", TransactionTypes)}";

    /// <summary>
    /// Structural validation of the rule itself, independent of any QuickBooks state.
    /// Returns an empty list when the job is well-formed.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (ToDate < FromDate)
        {
            errors.Add("End date must not be earlier than start date.");
        }

        if (TransactionTypes.Count == 0)
        {
            errors.Add("At least one transaction type must be selected.");
        }

        if (string.Equals(SourceAccount.ListId, DestinationAccount.ListId, StringComparison.Ordinal))
        {
            errors.Add("Destination account must differ from the source account.");
        }

        if (!DestinationAccount.IsActive)
        {
            errors.Add($"Destination account '{DestinationAccount.FullName}' is inactive in QuickBooks.");
        }

        if (!DestinationAccount.IsExpenseSide)
        {
            errors.Add(
                $"Destination account '{DestinationAccount.FullName}' is of type "
                + $"'{DestinationAccount.AccountType}', which cannot carry an expense line.");
        }

        if (BatchSize is < 1 or > 500)
        {
            errors.Add("Batch size must be between 1 and 500.");
        }

        return errors;
    }
}
