namespace QbReclass.Core.Model;

/// <summary>
/// QuickBooks transaction types this utility can reason about. Every member must have a
/// registered adapter before it can appear in a job; see <c>AdapterRegistry</c>.
/// </summary>
public enum TransactionType
{
    Unknown = 0,

    /// <summary>Credit card charge. Intuit documents <c>CreditCardChargeMod</c>.</summary>
    CreditCardCharge,

    /// <summary>Credit card credit/refund. Modifiable, but not enabled in version 1.</summary>
    CreditCardCredit,

    /// <summary>
    /// Check / Write Checks entry. Intuit's object matrix lists Check as queryable but exposes
    /// no <c>CheckMod</c> operation, so this type is preview-only. See spec section 11.
    /// </summary>
    Check,

    /// <summary>Bill. Out of scope for version 1.</summary>
    Bill,
}

/// <summary>Kind of line within a transaction. Only expense lines carry a reclassifiable account.</summary>
public enum LineKind
{
    /// <summary>An expense-account line (<c>ExpenseLineRet</c>). Reclassifiable.</summary>
    Expense = 0,

    /// <summary>An item line (<c>ItemLineRet</c>). Its account comes from the item, not the line.</summary>
    Item,

    /// <summary>An item group line (<c>ItemGroupLineRet</c>). Cannot be faithfully round-tripped.</summary>
    ItemGroup,
}

/// <summary>QuickBooks reconciliation state for a transaction, as reported by the SDK.</summary>
public enum ClearedStatus
{
    /// <summary>The SDK did not report a cleared status for this record.</summary>
    Unknown = 0,
    NotCleared,
    Cleared,
    Reconciled,
}

/// <summary>Whether a transaction type can be modified in place through a supported SDK request.</summary>
public enum ModificationSupport
{
    /// <summary>
    /// Intuit documents a Mod request for this type and the utility has an adapter that
    /// preserves unaffected lines.
    /// </summary>
    Supported = 0,

    /// <summary>
    /// No supported in-place Mod request exists. The record is shown in preview and never written.
    /// Spec FR-018: no silent fallback.
    /// </summary>
    NotSupported,

    /// <summary>
    /// The type has a Mod request but this particular record contains constructs the adapter
    /// cannot faithfully reconstruct (item groups, multicurrency, tax-inclusive lines).
    /// </summary>
    UnsupportedRecordShape,
}

/// <summary>Lifecycle of a reclassification job.</summary>
public enum JobStatus
{
    Draft = 0,
    Previewed,
    Executing,
    Paused,
    Completed,
    Abandoned,
}

/// <summary>Lifecycle of an approved batch.</summary>
public enum BatchStatus
{
    Pending = 0,
    Approved,
    Executing,
    Completed,
    Halted,
}

/// <summary>
/// State machine for a single candidate line. The ordering matters for restart reconciliation:
/// anything left in <see cref="Submitted"/> at startup has an unknown outcome in QuickBooks and
/// must be resolved by re-query before the job may continue (spec FR-016, section 18).
/// </summary>
public enum CandidateStatus
{
    /// <summary>Matched the rule, not yet selected by the user.</summary>
    Matched = 0,

    /// <summary>Selected by the user and attached to a batch.</summary>
    Selected,

    /// <summary>Preflight re-query succeeded and the record still matches the rule.</summary>
    PreflightPassed,

    /// <summary>
    /// A modify request was handed to QuickBooks but no response was durably recorded.
    /// The outcome is unknown until reconciled.
    /// </summary>
    Submitted,

    /// <summary>Modified and read-back verified.</summary>
    Verified,

    /// <summary>Deliberately not attempted (stale, unsupported, user-excluded, inactive account).</summary>
    Skipped,

    /// <summary>Attempted and rejected by QuickBooks, or verification failed.</summary>
    Failed,
}

/// <summary>Outcome of a single write attempt.</summary>
public enum ExecutionOutcome
{
    Success = 0,
    Skipped,
    Failed,

    /// <summary>Write reported success but read-back verification disagreed. Highest severity.</summary>
    VerificationMismatch,
}

/// <summary>What the execution loop does after a non-success result.</summary>
public enum StopPolicy
{
    /// <summary>Halt the batch on the first failure. Default; most conservative.</summary>
    StopOnFirstFailure = 0,

    /// <summary>Continue past isolated SDK validation failures, but never past a verification mismatch.</summary>
    ContinueOnIsolatedFailure,
}
