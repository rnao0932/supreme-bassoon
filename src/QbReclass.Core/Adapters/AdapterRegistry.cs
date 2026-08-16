using QbReclass.Core.Model;

namespace QbReclass.Core.Adapters;

/// <summary>
/// The set of transaction types this build understands. A type absent from the registry cannot be
/// put in a job at all, which is what stops the utility from generalizing credit-card behaviour to
/// types whose SDK mapping has never been proven (spec section 11).
/// </summary>
public sealed class AdapterRegistry
{
    private readonly Dictionary<TransactionType, ITransactionAdapter> _adapters;

    public AdapterRegistry(IEnumerable<ITransactionAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToDictionary(a => a.TxnType);
    }

    /// <summary>
    /// The shipping configuration: credit card charges are writable, checks are preview-only.
    /// </summary>
    public static AdapterRegistry Default { get; } = new(
    [
        new CreditCardChargeAdapter(),
        new CheckAdapter(),
    ]);

    public IReadOnlyCollection<ITransactionAdapter> All => _adapters.Values;

    /// <summary>Types that can appear in a preview.</summary>
    public IReadOnlyList<TransactionType> QueryableTypes => _adapters.Keys.ToList();

    /// <summary>Types that can actually be written.</summary>
    public IReadOnlyList<TransactionType> WritableTypes =>
        _adapters.Values
            .Where(a => a.TypeSupport == ModificationSupport.Supported)
            .Select(a => a.TxnType)
            .ToList();

    public bool TryGet(TransactionType txnType, out ITransactionAdapter adapter) =>
        _adapters.TryGetValue(txnType, out adapter!);

    public ITransactionAdapter Get(TransactionType txnType) =>
        _adapters.TryGetValue(txnType, out var adapter)
            ? adapter
            : throw new NotSupportedException(
                $"No adapter is registered for {txnType}. Each transaction type needs its own proven "
                + "SDK query/modification mapping before it can be used.");
}
