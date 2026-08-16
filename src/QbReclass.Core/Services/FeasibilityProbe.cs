using System.Text;
using QbReclass.Core.Adapters;
using QbReclass.Core.Model;
using QbReclass.Core.QbXml;
using QbReclass.Core.Session;

namespace QbReclass.Core.Services;

/// <summary>Severity of a single spike finding.</summary>
public enum ProbeSeverity
{
    Info = 0,
    Warning,
    Blocker,
}

/// <summary>One observation from the Phase 0 spike.</summary>
public sealed record ProbeFinding(string Area, ProbeSeverity Severity, string Message)
{
    public override string ToString() => $"[{Severity}] {Area}: {Message}";
}

/// <summary>What the spike is permitted to do.</summary>
public sealed record ProbeOptions
{
    /// <summary>
    /// TxnID of a credit card charge in a <b>disposable test company</b> to exercise the write path
    /// against. Null means the spike stays read-only.
    /// </summary>
    public string? WriteProbeTxnId { get; init; }

    /// <summary>
    /// ListID of an expense account to move a line to and back again. Required when
    /// <see cref="WriteProbeTxnId"/> is set.
    /// </summary>
    public string? WriteProbeDestinationListId { get; init; }

    /// <summary>
    /// Must be set explicitly to acknowledge that the connected file is disposable. The spike
    /// refuses to write without it (spec section 18: production testing is not an acceptable
    /// substitute for a test company).
    /// </summary>
    public bool ConfirmedDisposableTestCompany { get; init; }

    /// <summary>Date range used for the read probes.</summary>
    public DateOnly FromDate { get; init; } = new(DateTime.Today.Year - 1, 1, 1);

    public DateOnly ToDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>Everything the spike learned, ready to be written to a report.</summary>
public sealed record ProbeReport
{
    public required CompanyIdentity Company { get; init; }
    public required QbHostInfo Host { get; init; }
    public required string NegotiatedQbXmlVersion { get; init; }
    public required IReadOnlyList<ProbeFinding> Findings { get; init; }

    public int AccountCount { get; init; }
    public int ExpenseAccountCount { get; init; }

    /// <summary>Per transaction type: whether query worked and how many records came back.</summary>
    public IReadOnlyDictionary<TransactionType, string> TypeResults { get; init; } =
        new Dictionary<TransactionType, string>();

    public bool HasBlockers => Findings.Any(f => f.Severity == ProbeSeverity.Blocker);

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# QuickBooks Desktop SDK feasibility spike");
        sb.AppendLine();
        sb.AppendLine($"- Company: {Company.CompanyName}");
        sb.AppendLine($"- Company file: {Company.CompanyFileName}");
        sb.AppendLine($"- Product: {Host.ProductName ?? "(not reported)"}");
        sb.AppendLine($"- Version: {Host.MajorVersion}.{Host.MinorVersion}  Country: {Host.Country ?? "(not reported)"}");
        sb.AppendLine($"- File mode: {Host.FileMode ?? "(not reported)"}");
        sb.AppendLine($"- Supported qbXML versions: {string.Join(", ", Host.SupportedQbXmlVersions)}");
        sb.AppendLine($"- Negotiated qbXML version: {NegotiatedQbXmlVersion}");
        sb.AppendLine($"- Accounts: {AccountCount} ({ExpenseAccountCount} expense-side)");
        sb.AppendLine();
        sb.AppendLine("## Transaction types");
        sb.AppendLine();

        foreach (var (type, result) in TypeResults)
        {
            sb.AppendLine($"- **{type}**: {result}");
        }

        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();

        foreach (var finding in Findings)
        {
            sb.AppendLine($"- {finding}");
        }

        sb.AppendLine();
        sb.AppendLine(HasBlockers
            ? "**Result: blockers found. Do not proceed to the write path until they are resolved.**"
            : "**Result: no blockers found in the areas probed.**");

        return sb.ToString();
    }
}

/// <summary>
/// The Phase 0 technical spike (spec section 4, section 20 Phase 0).
/// </summary>
/// <remarks>
/// The spec is explicit that development begins with a spike rather than a user interface, and that
/// each target workflow must be proven against the actual QuickBooks edition rather than assumed.
/// This class is that spike, kept in the product rather than thrown away so it can be re-run on
/// each new workstation, QuickBooks upgrade or company file.
/// </remarks>
public sealed class FeasibilityProbe
{
    private readonly IQbSession _session;
    private readonly QueryService _query;
    private readonly AdapterRegistry _registry;
    private readonly Action<string>? _log;

    public FeasibilityProbe(
        IQbSession session,
        QueryService query,
        AdapterRegistry registry,
        Action<string>? log = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log;
    }

    public ProbeReport Run(ProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var findings = new List<ProbeFinding>();
        var typeResults = new Dictionary<TransactionType, string>();

        var host = _query.LoadHostInfo();
        var company = _query.LoadCompanyIdentity(host, _session.Info.Company.CompanyFileName);

        ProbeEnvironment(host, findings);

        var accounts = _query.LoadAccounts();
        var expenseAccounts = accounts.Where(a => a.IsExpenseSide).ToList();

        if (expenseAccounts.Count == 0)
        {
            findings.Add(new ProbeFinding(
                "Accounts", ProbeSeverity.Blocker, "No expense-side accounts were returned."));
        }

        foreach (var adapter in _registry.All)
        {
            ProbeType(adapter, options, findings, typeResults);
        }

        ProbeWritePath(options, findings);

        return new ProbeReport
        {
            Company = company,
            Host = host,
            NegotiatedQbXmlVersion = _session.Info.QbXmlVersion,
            Findings = findings,
            AccountCount = accounts.Count,
            ExpenseAccountCount = expenseAccounts.Count,
            TypeResults = typeResults,
        };
    }

    private static void ProbeEnvironment(QbHostInfo host, List<ProbeFinding> findings)
    {
        findings.Add(new ProbeFinding(
            "Environment",
            ProbeSeverity.Info,
            $"Product '{host.ProductName}', version {host.MajorVersion}.{host.MinorVersion}, "
            + $"country {host.Country}, file mode {host.FileMode}."));

        if (string.Equals(host.FileMode, "MultiUser", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new ProbeFinding(
                "Environment",
                ProbeSeverity.Warning,
                "QuickBooks is in multi-user mode. Confirm which modifications are permitted in this "
                + "mode and whether another user can edit a transaction between preflight and write "
                + "(spec section 22, final question)."));
        }

        if (host.SupportedQbXmlVersions.Count == 0)
        {
            findings.Add(new ProbeFinding(
                "Environment",
                ProbeSeverity.Blocker,
                "QuickBooks reported no supported qbXML versions."));
        }
    }

    private void ProbeType(
        ITransactionAdapter adapter,
        ProbeOptions options,
        List<ProbeFinding> findings,
        Dictionary<TransactionType, string> typeResults)
    {
        try
        {
            var sample = _query
                .QueryTransactions(adapter.TxnType, options.FromDate, options.ToDate, pageSize: 25)
                .Take(25)
                .ToList();

            var supportText = adapter.TypeSupport == ModificationSupport.Supported
                ? $"query OK, modifiable via {adapter.ModRequestName}"
                : $"query OK, NOT modifiable ({adapter.TypeUnsupportedReason})";

            typeResults[adapter.TxnType] = $"{sample.Count} sampled; {supportText}";

            ProbeClearedStatus(adapter, sample, findings);
            ProbeRecordShapes(adapter, sample, findings);
        }
        catch (Exception ex) when (ex is QbQueryException or QbXmlFormatException or QbSessionException)
        {
            typeResults[adapter.TxnType] = $"query FAILED: {ex.Message}";
            findings.Add(new ProbeFinding(
                adapter.DisplayName,
                ProbeSeverity.Blocker,
                $"Query failed against this QuickBooks: {ex.Message}"));
        }
    }

    /// <summary>
    /// Spec section 22 question 4 asks how QuickBooks exposes reconciliation state for the target
    /// records. This answers it empirically for the workstation the spike runs on.
    /// </summary>
    private static void ProbeClearedStatus(
        ITransactionAdapter adapter,
        IReadOnlyList<TransactionSnapshot> sample,
        List<ProbeFinding> findings)
    {
        if (sample.Count == 0)
        {
            return;
        }

        var reported = sample.Count(s => s.Cleared != ClearedStatus.Unknown);

        findings.Add(reported == 0
            ? new ProbeFinding(
                adapter.DisplayName,
                ProbeSeverity.Warning,
                $"None of the {sample.Count} sampled records reported a cleared/reconciled status. "
                + "The preview cannot show a reliable reconciled count for this type, and the batch "
                + "approval screen must say so rather than imply zero (spec section 9).")
            : new ProbeFinding(
                adapter.DisplayName,
                ProbeSeverity.Info,
                $"{reported} of {sample.Count} sampled records reported a cleared/reconciled status."));
    }

    private static void ProbeRecordShapes(
        ITransactionAdapter adapter,
        IReadOnlyList<TransactionSnapshot> sample,
        List<ProbeFinding> findings)
    {
        if (adapter.TypeSupport != ModificationSupport.Supported || sample.Count == 0)
        {
            return;
        }

        var unsupported = sample
            .Select(s => (Snapshot: s, Support: adapter.EvaluateRecord(s)))
            .Where(x => !x.Support.CanWrite)
            .ToList();

        if (unsupported.Count > 0)
        {
            var reasons = unsupported
                .Select(x => x.Support.Reason ?? "unknown")
                .Distinct(StringComparer.Ordinal);

            findings.Add(new ProbeFinding(
                adapter.DisplayName,
                ProbeSeverity.Warning,
                $"{unsupported.Count} of {sample.Count} sampled records cannot be modified by this "
                + $"version. Reasons: {string.Join(" | ", reasons)}"));
        }

        var withItems = sample.Count(s => s.HasItemLines);
        if (withItems > 0)
        {
            findings.Add(new ProbeFinding(
                adapter.DisplayName,
                ProbeSeverity.Info,
                $"{withItems} of {sample.Count} sampled records carry item lines. Item lines are "
                + "resubmitted verbatim to retain them; confirm this against a test record before "
                + "trusting the write path (spec section 22, question 6)."));
        }
    }

    /// <summary>
    /// Exercises the modification request end to end by reclassifying a line and putting it back.
    /// </summary>
    /// <remarks>
    /// This is the part of the spike that actually proves <c>CreditCardChargeMod</c> on the target
    /// installation, including line preservation. It writes, so it demands an explicit
    /// acknowledgement that the connected file is disposable.
    /// </remarks>
    private void ProbeWritePath(ProbeOptions options, List<ProbeFinding> findings)
    {
        if (options.WriteProbeTxnId is null)
        {
            findings.Add(new ProbeFinding(
                "Write path",
                ProbeSeverity.Warning,
                "The write path was not exercised. Re-run the spike against a disposable test "
                + "company with a credit card charge TxnID before treating the modification path as "
                + "proven (spec section 4)."));
            return;
        }

        if (!options.ConfirmedDisposableTestCompany)
        {
            findings.Add(new ProbeFinding(
                "Write path",
                ProbeSeverity.Blocker,
                "A write probe was requested without confirming the company file is disposable. "
                + "Refused."));
            return;
        }

        if (string.IsNullOrEmpty(options.WriteProbeDestinationListId))
        {
            findings.Add(new ProbeFinding(
                "Write path", ProbeSeverity.Blocker, "A write probe needs a destination account ListID."));
            return;
        }

        var adapter = _registry.Get(TransactionType.CreditCardCharge);

        try
        {
            var before = _query.GetTransaction(TransactionType.CreditCardCharge, options.WriteProbeTxnId);
            if (before is null)
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Blocker,
                    $"Credit card charge {options.WriteProbeTxnId} was not found."));
                return;
            }

            var support = adapter.EvaluateRecord(before);
            if (!support.CanWrite)
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Blocker,
                    $"The nominated record cannot be modified by this version: {support.Reason}"));
                return;
            }

            var targetLine = before.ExpenseLines.First();
            var originalAccount = targetLine.Account;
            var targetIds = new[] { targetLine.TxnLineId! };
            var destination = new QbRef(options.WriteProbeDestinationListId, null);

            // Out: move the line to the probe destination.
            var outResponse = _query.Send(adapter.BuildReclassification(before, targetIds, destination));
            if (!outResponse.Status.IsOk)
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Blocker,
                    $"{adapter.ModRequestName} was rejected: {outResponse.Status}. This is the "
                    + "feasibility question the spec raises in section 4; record the exact status "
                    + "code and message before designing around it."));
                return;
            }

            var afterOut = _query.GetTransaction(TransactionType.CreditCardCharge, options.WriteProbeTxnId)!;
            var outReport = ProtectedFields.Compare(before, afterOut, targetIds, options.WriteProbeDestinationListId);

            findings.Add(outReport.Passed
                ? new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Info,
                    $"{adapter.ModRequestName} reclassified line {targetLine.TxnLineId} and left every "
                    + $"protected field and all {before.Lines.Count} line(s) intact.")
                : new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Blocker,
                    $"{adapter.ModRequestName} was accepted but the result was not what was intended: "
                    + outReport.Describe()));

            // Back: restore the original account so the test company is left as it was found.
            var backResponse = _query.Send(adapter.BuildReclassification(afterOut, targetIds, originalAccount));
            if (!backResponse.Status.IsOk)
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Blocker,
                    $"Could not restore the original account on {options.WriteProbeTxnId}: "
                    + $"{backResponse.Status}. The test record has been left reclassified."));
                return;
            }

            var restored = _query.GetTransaction(TransactionType.CreditCardCharge, options.WriteProbeTxnId)!;
            var restoreReport = ProtectedFields.Compare(afterOut, restored, targetIds, originalAccount.ListId!);

            findings.Add(restoreReport.Passed
                ? new ProbeFinding("Write path", ProbeSeverity.Info, "Original account restored cleanly.")
                : new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Warning,
                    "Restore completed with differences: " + restoreReport.Describe()));

            if (before.IsReconciledOrCleared)
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Info,
                    $"The probe record was {before.Cleared}. Cleared status after the round trip: "
                    + $"{restored.Cleared}. Compare a reconciliation report before and after before "
                    + "relying on this (spec section 18)."));
            }
            else
            {
                findings.Add(new ProbeFinding(
                    "Write path",
                    ProbeSeverity.Warning,
                    "The probe record was not reconciled or cleared, so the round trip proves nothing "
                    + "about reconciled transactions. Re-run against a reconciled record."));
            }
        }
        catch (Exception ex) when (ex is QbQueryException or QbXmlFormatException or QbSessionException or NotSupportedException)
        {
            findings.Add(new ProbeFinding("Write path", ProbeSeverity.Blocker, ex.Message));
            _log?.Invoke(ex.ToString());
        }
    }
}
