using System.Globalization;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;

namespace QbReclass.Simulator;

/// <summary>
/// An in-memory stand-in for a QuickBooks company file.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the utility's safety behaviour can be tested without QuickBooks: conflict
/// detection, line preservation, restart reconciliation and read-back verification all need a
/// counterpart that behaves like QuickBooks, including the parts that are dangerous.
/// </para>
/// <para>
/// It is a test double, not a specification. It encodes this project's current understanding of
/// qbXML semantics; where that understanding is wrong, the Phase 0 spike against real QuickBooks
/// is what will say so. Passing tests here are necessary, not sufficient.
/// </para>
/// </remarks>
public sealed class SimulatedCompany
{
    private int _editCounter;
    private int _txnCounter;
    private int _listCounter;

    public SimulatedCompany(string companyName = "Test Company Inc.", string? filePath = null)
    {
        Identity = new CompanyIdentity
        {
            CompanyName = companyName,
            LegalCompanyName = companyName,
            CompanyFileName = filePath ?? @"C:\QuickBooks\TestCompany.QBW",
            Ein = "00-0000000",
            ProductName = "QuickBooks Desktop Enterprise Solutions 24.0 (simulated)",
            FileMode = "SingleUser",
        };

        Host = new QbHostInfo
        {
            ProductName = Identity.ProductName,
            MajorVersion = "34",
            MinorVersion = "0",
            Country = "US",
            FileMode = "SingleUser",
            SupportedQbXmlVersions = ["13.0", "14.0", "15.0", "16.0"],
        };
    }

    public CompanyIdentity Identity { get; set; }

    public QbHostInfo Host { get; set; }

    public List<QbAccount> Accounts { get; } = [];

    public List<TransactionSnapshot> Transactions { get; } = [];

    /// <summary>
    /// When false, the simulated QuickBooks omits <c>ClearedStatus</c> from every transaction, the
    /// way some qbXML versions do. Used to prove the utility degrades honestly rather than
    /// reporting "not reconciled" (spec section 9, section 22 question 4).
    /// </summary>
    public bool ReportsClearedStatus { get; set; } = true;

    /// <summary>Records returned per iterator page.</summary>
    public int PageSizeCap { get; set; } = 100;

    internal string NextEditSequence() =>
        (++_editCounter).ToString(CultureInfo.InvariantCulture);

    /// <summary>Adds an account and returns it.</summary>
    public QbAccount AddAccount(string fullName, string accountType = "Expense", bool isActive = true)
    {
        var account = new QbAccount
        {
            ListId = $"ACC-{++_listCounter:D4}",
            FullName = fullName,
            AccountType = accountType,
            IsActive = isActive,
            EditSequence = NextEditSequence(),
        };

        Accounts.Add(account);
        return account;
    }

    public QbAccount GetAccount(string listId) =>
        Accounts.FirstOrDefault(a => a.ListId == listId)
        ?? throw new InvalidOperationException($"Simulated company has no account '{listId}'.");

    /// <summary>Makes an account inactive, as a user would in QuickBooks.</summary>
    public void DeactivateAccount(string listId)
    {
        var index = Accounts.FindIndex(a => a.ListId == listId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Simulated company has no account '{listId}'.");
        }

        Accounts[index] = Accounts[index] with { IsActive = false, EditSequence = NextEditSequence() };
    }

    /// <summary>Adds a transaction with the given expense lines.</summary>
    public TransactionSnapshot AddTransaction(
        TransactionType txnType,
        QbAccount postingAccount,
        DateOnly date,
        string payee,
        IEnumerable<(QbAccount Account, decimal Amount, string? Memo)> expenseLines,
        string? refNumber = null,
        string? memo = null,
        ClearedStatus cleared = ClearedStatus.NotCleared,
        string? className = null)
    {
        var txnId = $"TXN-{++_txnCounter:D5}";
        var ordinal = 0;

        var lines = expenseLines
            .Select(l => new TransactionLineSnapshot
            {
                TxnLineId = $"{txnId}-L{++ordinal}",
                Kind = LineKind.Expense,
                Ordinal = ordinal - 1,
                Account = QbRef.FromAccount(l.Account),
                Amount = l.Amount,
                Memo = l.Memo,
                Class = className is null ? QbRef.Empty : new QbRef($"CLS-{className}", className),
                BillableStatus = "NotBillable",
            })
            .ToList();

        var snapshot = new TransactionSnapshot
        {
            TxnId = txnId,
            EditSequence = NextEditSequence(),
            TxnType = txnType,
            TxnDate = date,
            RefNumber = refNumber,
            Memo = memo,
            Payee = new QbRef($"VEND-{payee.GetHashCode(StringComparison.Ordinal):X}", payee),
            PostingAccount = QbRef.FromAccount(postingAccount),
            TotalAmount = lines.Sum(l => l.Amount),
            Cleared = cleared,
            TimeCreated = DateTimeOffset.UtcNow,
            TimeModified = DateTimeOffset.UtcNow,
            Lines = lines,
        };

        Transactions.Add(snapshot);
        return snapshot;
    }

    /// <summary>Appends an item line to an existing transaction.</summary>
    public void AddItemLine(string txnId, string itemName, decimal amount, decimal? quantity = null)
    {
        var index = Transactions.FindIndex(t => t.TxnId == txnId);
        var txn = Transactions[index];

        var lines = txn.Lines.ToList();
        lines.Add(new TransactionLineSnapshot
        {
            TxnLineId = $"{txnId}-I{lines.Count + 1}",
            Kind = LineKind.Item,
            Ordinal = lines.Count,
            Item = new QbRef($"ITEM-{itemName}", itemName),
            Amount = amount,
            Quantity = quantity,
            BillableStatus = "NotBillable",
        });

        Transactions[index] = txn with
        {
            Lines = lines,
            TotalAmount = lines.Sum(l => l.Amount),
            EditSequence = NextEditSequence(),
        };
    }

    /// <summary>Appends an item group line, which the adapters must refuse to modify.</summary>
    public void AddItemGroupLine(string txnId, string groupName, decimal amount)
    {
        var index = Transactions.FindIndex(t => t.TxnId == txnId);
        var txn = Transactions[index];

        var lines = txn.Lines.ToList();
        lines.Add(new TransactionLineSnapshot
        {
            TxnLineId = $"{txnId}-G{lines.Count + 1}",
            Kind = LineKind.ItemGroup,
            Ordinal = lines.Count,
            Item = new QbRef($"GRP-{groupName}", groupName),
            Amount = amount,
        });

        Transactions[index] = txn with
        {
            Lines = lines,
            TotalAmount = lines.Sum(l => l.Amount),
            EditSequence = NextEditSequence(),
        };
    }

    public TransactionSnapshot? Find(string txnId) =>
        Transactions.FirstOrDefault(t => t.TxnId == txnId);

    /// <summary>
    /// Overwrites a transaction without touching its EditSequence. Used by tests to reproduce a
    /// QuickBooks that quietly changed something it should not have.
    /// </summary>
    public void Replace(TransactionSnapshot snapshot)
    {
        var index = Transactions.FindIndex(t => t.TxnId == snapshot.TxnId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Simulated company has no transaction '{snapshot.TxnId}'.");
        }

        Transactions[index] = snapshot;
    }

    /// <summary>
    /// Applies a change the way a person working in QuickBooks would: the record changes and its
    /// EditSequence moves. Used to reproduce the spec's "edited after preview" test (section 18).
    /// </summary>
    public void EditExternally(string txnId, Func<TransactionSnapshot, TransactionSnapshot> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        var current = Find(txnId)
            ?? throw new InvalidOperationException($"Simulated company has no transaction '{txnId}'.");

        Replace(mutate(current) with
        {
            EditSequence = NextEditSequence(),
            TimeModified = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Deletes a transaction, as a user could between preview and execution.</summary>
    public void DeleteTransaction(string txnId) =>
        Transactions.RemoveAll(t => t.TxnId == txnId);
}
