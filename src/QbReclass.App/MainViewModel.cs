using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using QbReclass.Core.Adapters;
using QbReclass.Core.Audit;
using QbReclass.Core.Model;
using QbReclass.Core.Services;
using QbReclass.Core.Session;
using QbReclass.QuickBooks;
using QbReclass.Simulator;

namespace QbReclass.App;

/// <summary>
/// Drives the whole application: connect, define a rule, preview, approve, execute, verify.
/// </summary>
/// <remarks>
/// The ordering of the guards here is the user-facing half of the spec's safety model. The
/// application opens read-only (FR-002); write mode needs a backup confirmation (section 6 step 2);
/// a batch needs an explicit approval on a screen that states its scope (section 9); and no batch
/// starts on its own (section 6 step 12).
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly StaWorker _worker = new();
    private AdapterRegistry _registry = AdapterRegistry.Default;

    private IQbSession? _session;
    private SqliteAuditStore? _store;
    private QueryService? _query;
    private ReclassificationPlanner? _planner;
    private PreflightService? _preflight;
    private VerificationService? _verification;
    private ExecutionService? _execution;
    private ReconciliationService? _reconciliation;
    private ExportService? _export;
    private readonly BatchPlanner _batches = new();

    private CancellationTokenSource? _stop;
    private Job? _job;

    private string _companyDisplay = "Not connected";
    private string _statusText = "Ready. The application is in read-only mode.";
    private string _detailText = "Select a row to see its full transaction identity.";
    private bool _isBusy;
    private bool _writeModeEnabled;
    private bool _backupConfirmed;
    private bool _isConnected;
    private DateTime _fromDate = new(DateTime.Today.Year, 1, 1);
    private DateTime _toDate = DateTime.Today;
    private QbAccount? _sourceAccount;
    private QbAccount? _destinationAccount;
    private bool _includeCreditCardCharges = true;
    private bool _includeChecks;
    private string _payeeFilter = string.Empty;
    private string _memoFilter = string.Empty;
    private string _referenceFilter = string.Empty;
    private string _minAmount = string.Empty;
    private string _maxAmount = string.Empty;
    private int _batchSize = 25;
    private bool _continueOnIsolatedFailure;
    private bool _enableCheckWrites;
    private bool _enableBillWrites;
    private bool _includeBills;
    private string _companyFilePath = string.Empty;
    private CandidateRow? _selectedRow;

    public MainViewModel()
    {
        // Two explicit commands rather than one button plus a "use the simulator" checkbox. The
        // checkbox was easy to miss, and missing it meant an attempt against real QuickBooks and a
        // COM error, which is a poor way to learn you wanted the other mode.
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(simulate: false), () => !IsBusy && !IsConnected);
        ConnectSimulatedCommand = new RelayCommand(() => _ = ConnectAsync(simulate: true), () => !IsBusy && !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => !IsBusy && IsConnected);
        PreviewCommand = new RelayCommand(() => _ = PreviewAsync(), () => !IsBusy && IsConnected && CanPreview);
        SelectAllCommand = new RelayCommand(() => SetSelection(true), () => !IsBusy && Rows.Count > 0);
        SelectNoneCommand = new RelayCommand(() => SetSelection(false), () => !IsBusy && Rows.Count > 0);
        RunBatchCommand = new RelayCommand(() => _ = RunBatchAsync(), () => !IsBusy && CanRunBatch);
        StopCommand = new RelayCommand(() => _stop?.Cancel(), () => IsBusy && _stop is not null);
        ExportCommand = new RelayCommand(Export, () => !IsBusy && _job is not null);
        OpenInQuickBooksCommand = new RelayCommand(() => _ = OpenInQuickBooksAsync(), () => !IsBusy && SelectedRow is not null);
        BrowseCompanyFileCommand = new RelayCommand(BrowseCompanyFile, () => !IsBusy && !IsConnected);
    }

    // ---------------------------------------------------------------- bindings

    public ObservableCollection<CandidateRow> Rows { get; } = [];

    public ObservableCollection<QbAccount> Accounts { get; } = [];

    /// <summary>
    /// Things the operator needs to know about this preview that the grid cannot show: a
    /// reconciliation status QuickBooks did not report, a chart of accounts with nothing
    /// reclassifiable in it. Shown as banners above the grid.
    /// </summary>
    public ObservableCollection<string> Notices { get; } = [];

    public RelayCommand ConnectCommand { get; }
    public RelayCommand ConnectSimulatedCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand RunBatchCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenInQuickBooksCommand { get; }
    public RelayCommand BrowseCompanyFileCommand { get; }

    public string CompanyDisplay
    {
        get => _companyDisplay;
        private set => Set(ref _companyDisplay, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(IsIdle));
                Raise(nameof(CanChangeConnectionOptions));
                RefreshCommands();
            }
        }
    }

    public bool IsIdle => !IsBusy;

    /// <summary>
    /// Whether the connection-time options can still be changed.
    /// </summary>
    /// <remarks>
    /// Write mode and check-write support are fixed when the session opens: the session is opened
    /// read-only or not, and the adapters that decide which rows are writable are built at the same
    /// moment. Toggling either afterwards cannot take effect, so the controls are disabled rather
    /// than left clickable and then answered with a dialog explaining that nothing happened.
    /// </remarks>
    public bool CanChangeConnectionOptions => !IsBusy && !IsConnected;

    /// <summary>
    /// Optional path to a .QBW file.
    /// </summary>
    /// <remarks>
    /// Empty means "whatever QuickBooks currently has open", which is the safer default because it
    /// cannot open a file the operator did not intend. Naming a file is for the case QuickBooks
    /// reports when nothing is loaded - it will open the named file itself - and for pointing
    /// deliberately at a restored copy rather than the live company.
    /// </remarks>
    public string CompanyFilePath
    {
        get => _companyFilePath;
        set => Set(ref _companyFilePath, value);
    }


    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
            {
                Raise(nameof(CanChangeConnectionOptions));
                RefreshCommands();
            }
        }
    }

    /// <summary>
    /// Write mode. Cannot be turned on until a backup has been confirmed, and turning it off is
    /// always allowed.
    /// </summary>
    public bool WriteModeEnabled
    {
        get => _writeModeEnabled;
        set
        {
            if (value && !BackupConfirmed)
            {
                MessageBox.Show(
                    "Confirm that a current QuickBooks backup exists before enabling write mode.",
                    "Backup required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Raise(nameof(WriteModeEnabled));
                return;
            }

            if (value && IsConnected && _session?.Info.IsReadOnly == true)
            {
                MessageBox.Show(
                    "This QuickBooks session was opened read-only. Disconnect and reconnect with write "
                    + "access enabled.",
                    "Read-only session",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Raise(nameof(WriteModeEnabled));
                return;
            }

            if (Set(ref _writeModeEnabled, value))
            {
                StatusText = value
                    ? "Write mode enabled. Nothing changes until you approve a batch."
                    : "Read-only mode.";
                RefreshCommands();
            }
        }
    }

    /// <summary>
    /// The user's affirmative confirmation that a current backup exists. The utility records when
    /// it was given; it does not create the backup (spec section 6 step 2).
    /// </summary>
    public bool BackupConfirmed
    {
        get => _backupConfirmed;
        set
        {
            if (Set(ref _backupConfirmed, value))
            {
                BackupConfirmedAt = value ? DateTimeOffset.Now : null;
                Raise(nameof(BackupConfirmedText));

                if (!value)
                {
                    WriteModeEnabled = false;
                }

                RefreshCommands();
            }
        }
    }

    public DateTimeOffset? BackupConfirmedAt { get; private set; }

    public string BackupConfirmedText => BackupConfirmedAt is { } at
        ? $"Backup confirmed at {at:yyyy-MM-dd HH:mm:ss}"
        : "No backup confirmed.";

    public DateTime FromDate
    {
        get => _fromDate;
        set { if (Set(ref _fromDate, value)) { RefreshCommands(); } }
    }

    public DateTime ToDate
    {
        get => _toDate;
        set { if (Set(ref _toDate, value)) { RefreshCommands(); } }
    }

    public QbAccount? SourceAccount
    {
        get => _sourceAccount;
        set { if (Set(ref _sourceAccount, value)) { RefreshCommands(); } }
    }

    public QbAccount? DestinationAccount
    {
        get => _destinationAccount;
        set { if (Set(ref _destinationAccount, value)) { RefreshCommands(); } }
    }

    public bool IncludeCreditCardCharges
    {
        get => _includeCreditCardCharges;
        set { if (Set(ref _includeCreditCardCharges, value)) { RefreshCommands(); } }
    }

    public bool IncludeChecks
    {
        get => _includeChecks;
        set { if (Set(ref _includeChecks, value)) { RefreshCommands(); } }
    }

    public string PayeeFilter { get => _payeeFilter; set => Set(ref _payeeFilter, value); }
    public string MemoFilter { get => _memoFilter; set => Set(ref _memoFilter, value); }
    public string ReferenceFilter { get => _referenceFilter; set => Set(ref _referenceFilter, value); }
    public string MinAmount { get => _minAmount; set => Set(ref _minAmount, value); }
    public string MaxAmount { get => _maxAmount; set => Set(ref _maxAmount, value); }

    public int BatchSize
    {
        get => _batchSize;
        set => Set(ref _batchSize, Math.Clamp(value, 1, 500));
    }

    /// <summary>
    /// Whether checks may be modified. Fixed for the life of a connection, because the adapters are
    /// built when the session opens and the preview's supported/unsupported markings come from them;
    /// letting it change underneath a preview would leave rows labelled by a rule no longer in force.
    /// </summary>
    public bool EnableCheckWrites
    {
        get => _enableCheckWrites;
        set
        {
            if (Set(ref _enableCheckWrites, value) && IsConnected)
            {
                StatusText = "Check writes "
                    + (value ? "enabled" : "disabled")
                    + " takes effect on the next connection. Disconnect and reconnect to apply it.";
            }
        }
    }

    /// <summary>Whether bills may be modified. Fixed for the life of a connection, like check writes.</summary>
    public bool EnableBillWrites
    {
        get => _enableBillWrites;
        set => Set(ref _enableBillWrites, value);
    }

    public bool IncludeBills
    {
        get => _includeBills;
        set { if (Set(ref _includeBills, value)) { RefreshCommands(); } }
    }

    public bool ContinueOnIsolatedFailure
    {
        get => _continueOnIsolatedFailure;
        set => Set(ref _continueOnIsolatedFailure, value);
    }

    public CandidateRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value))
            {
                DetailText = value?.Detail ?? "Select a row to see its full transaction identity.";
                RefreshCommands();
            }
        }
    }

    public int MatchCount => Rows.Count;

    public decimal MatchTotal => Rows.Sum(r => r.Amount);

    public int SelectedCount => Rows.Count(r => r.IsSelected);

    public decimal SelectedTotal => Rows.Where(r => r.IsSelected).Sum(r => r.Amount);

    public int UnsupportedCount => Rows.Count(r => !r.CanSelect);

    public string TotalsText =>
        $"{MatchCount:N0} matching line(s), {MatchTotal:N2} total  |  "
        + $"{SelectedCount:N0} selected, {SelectedTotal:N2}  |  "
        + $"{UnsupportedCount:N0} cannot be changed";

    private bool CanPreview =>
        SourceAccount is not null
        && DestinationAccount is not null
        && (IncludeCreditCardCharges || IncludeChecks || IncludeBills)
        && FromDate <= ToDate;

    private bool CanRunBatch =>
        IsConnected && WriteModeEnabled && BackupConfirmed && _job is not null && SelectedCount > 0;

    // ---------------------------------------------------------------- operations

    private async Task ConnectAsync(bool simulate)
    {
        IsBusy = true;
        StatusText = simulate
            ? "Opening the simulated company..."
            : "Connecting to QuickBooks...";

        try
        {
            var wantsWrite = WriteModeEnabled;
            var companyFile = CompanyFilePath.Trim();
            _registry = AdapterRegistry.Create(EnableCheckWrites, EnableBillWrites);

            var session = await _worker.RunAsync<IQbSession>(() => simulate
                ? new SimulatedQbSession(DemoCompany.Build(), readOnly: !wantsWrite)
                : QbComSession.Connect(new QbConnectionOptions
                {
                    ReadOnly = !wantsWrite,
                    CompanyFilePath = companyFile,
                }));

            _session = session;
            _store = SqliteAuditStore.OpenDefault();
            _query = new QueryService(session, _registry);
            _planner = new ReclassificationPlanner(_registry);
            _preflight = new PreflightService(_query, _registry);
            _verification = new VerificationService(_query);
            _execution = new ExecutionService(session, _query, _preflight, _verification, _registry, _store);
            _reconciliation = new ReconciliationService(_query, _store);
            _export = new ExportService(_store);

            var accounts = await _worker.RunAsync(() => _query.LoadAccounts());

            Notices.Clear();
            Accounts.Clear();

            // Every account is listed, expense-side ones first, rather than silently filtering to
            // the types this build recognizes. A chart of accounts using a type not in that set
            // would otherwise produce an empty dropdown and no way to tell why. Choosing an
            // unsuitable destination is caught by Job.Validate with a message that names the type.
            foreach (var account in accounts
                         .OrderByDescending(a => a.IsExpenseSide)
                         .ThenByDescending(a => a.IsActive)
                         .ThenBy(a => a.FullName, StringComparer.Ordinal))
            {
                Accounts.Add(account);
            }

            var reclassifiable = accounts.Count(a => a.IsExpenseSide && a.IsActive);
            if (reclassifiable == 0)
            {
                Notices.Add(
                    $"None of the {accounts.Count} accounts QuickBooks returned is an active "
                    + "expense-side account, so there is nothing this rule can reclassify to. The "
                    + "account types seen were: "
                    + string.Join(", ", accounts.Select(a => a.AccountType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
                    + ".");
            }

            CompanyDisplay =
                $"{session.Info.Company.CompanyName}   |   {session.Info.Company.CompanyFileName}   |   "
                + $"qbXML {session.Info.QbXmlVersion}   |   {(session.Info.IsReadOnly ? "READ-ONLY SESSION" : "write enabled")}";

            IsConnected = true;
            StatusText = $"Connected. {Accounts.Count} account(s) loaded, {reclassifiable} of them reclassifiable.";

            await ReconcileInterruptedWorkAsync();
        }
        catch (Exception ex)
        {
            ShowError("Could not connect to QuickBooks", ex);
            Disconnect();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// On connect, resolve any write that was in flight when the application last stopped
    /// (spec FR-016).
    /// </summary>
    private async Task ReconcileInterruptedWorkAsync()
    {
        if (_store is null || _reconciliation is null || _session is null)
        {
            return;
        }

        var jobs = _store.ListJobs()
            .Where(j => j.Company.Matches(_session.Info.Company))
            .ToList();

        foreach (var job in jobs)
        {
            if (_store.GetCandidatesByStatus(job.JobId, CandidateStatus.Submitted).Count == 0)
            {
                continue;
            }

            var outcome = await _worker.RunAsync(() => _reconciliation.Reconcile(job));

            MessageBox.Show(
                $"Job {job.JobId} had writes in progress when the application last stopped.{Environment.NewLine}"
                + Environment.NewLine
                + outcome.Summary
                + (outcome.NeedsAttention
                    ? Environment.NewLine + Environment.NewLine
                      + "Some records need review in QuickBooks before this job continues."
                    : string.Empty),
                "Interrupted work reconciled",
                MessageBoxButton.OK,
                outcome.NeedsAttention ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }

    private void Disconnect()
    {
        _session?.Dispose();
        _store?.Dispose();

        _session = null;
        _store = null;
        _query = null;
        _execution = null;
        _job = null;

        Rows.Clear();
        Accounts.Clear();
        Notices.Clear();
        IsConnected = false;
        CompanyDisplay = "Not connected";
        StatusText = "Disconnected.";
        RaiseTotals();
    }

    private async Task PreviewAsync()
    {
        if (_query is null || _planner is null || _store is null || _session is null)
        {
            return;
        }

        var job = BuildJob();
        var errors = job.Validate();

        if (errors.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, errors), "Rule is not valid",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        StatusText = "Reading matching transactions. Nothing is being changed.";

        try
        {
            var plan = await _worker.RunAsync(() =>
            {
                var transactions = job.TransactionTypes
                    .SelectMany(t => _query.QueryTransactions(t, job.FromDate, job.ToDate, job.Filters))
                    .ToList();

                var result = _planner.Plan(job, transactions);
                _store.SaveJob(job);
                _store.SaveCandidates(job.JobId, result.Candidates);
                return result;
            });

            _job = job;

            Rows.Clear();
            foreach (var candidate in plan.Candidates)
            {
                var row = new CandidateRow(candidate);
                row.SelectionChanged += (_, _) => RaiseTotals();
                Rows.Add(row);
            }

            Notices.Clear();

            if (plan.ClearedStatusUnavailable)
            {
                Notices.Add(
                    "QuickBooks did not report a reconciliation status for these records, so the "
                    + "reconciled count is not available. Treat every record as potentially reconciled.");
            }

            if (plan.UnsupportedCount > 0)
            {
                Notices.Add(
                    $"{plan.UnsupportedCount} of {plan.MatchCount} matching line(s) cannot be changed "
                    + "by this utility and are shown for review only. Click one to see why.");
            }

            StatusText =
                $"Preview complete: {plan.MatchCount:N0} line(s) across {plan.TransactionsExamined:N0} "
                + $"transaction(s). Nothing has been changed.";

            RaiseTotals();
        }
        catch (Exception ex)
        {
            ShowError("Preview failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetSelection(bool selected)
    {
        foreach (var row in Rows.Where(r => r.CanSelect))
        {
            row.IsSelected = selected;
        }

        RaiseTotals();
    }

    private async Task RunBatchAsync()
    {
        if (_job is null || _store is null || _execution is null)
        {
            return;
        }

        var job = _job with { BackupConfirmedAt = BackupConfirmedAt, BatchSize = BatchSize };
        _store.SaveJob(job);
        _job = job;

        var chosen = Rows.Where(r => r.IsSelected).Select(r => r.Candidate.CandidateId).ToList();
        _store.SetSelection(Rows.Select(r => r.Candidate.CandidateId), false);
        _store.SetSelection(chosen, true);

        var selected = _store.GetCandidates(job.JobId).Where(c => c.IsSelected).ToList();
        var batches = _batches.SplitIntoBatches(job, selected);

        if (batches.Count == 0)
        {
            MessageBox.Show("Nothing selected can be written.", "Nothing to do", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // One batch per click. The next batch never starts on its own (spec section 6 step 12).
        var pending = batches[0];
        var summary = _batches.Summarize(job, pending, selected);

        var dialog = new BatchApprovalWindow(summary) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            StatusText = "Batch not approved. Nothing was changed.";
            return;
        }

        var approved = _batches.Approve(pending, Environment.UserName);
        _store.SaveBatch(approved);

        IsBusy = true;
        _stop = new CancellationTokenSource();
        RefreshCommands();

        try
        {
            var progress = new Progress<ExecutionProgress>(p =>
                StatusText = $"[{p.Completed}/{p.Total}] {p.CurrentTxnId}: {p.LastResult?.Describe()}");

            var token = _stop.Token;
            var outcome = await _worker.RunAsync(() => _execution.Execute(job, approved, progress, token));

            RefreshRows();

            StatusText = outcome.Summary;

            if (outcome.MismatchCount > 0)
            {
                MessageBox.Show(
                    "A modification was accepted by QuickBooks but the transaction did not end up as "
                    + $"intended.{Environment.NewLine}{Environment.NewLine}{outcome.HaltReason}"
                    + $"{Environment.NewLine}{Environment.NewLine}"
                    + "The batch has been stopped. Review this transaction in QuickBooks before continuing.",
                    "Verification mismatch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else if (outcome.Halted)
            {
                MessageBox.Show(
                    $"{outcome.HaltReason}{Environment.NewLine}{Environment.NewLine}"
                    + $"{outcome.UnattemptedCandidateIds.Count} line(s) in this batch were not attempted.",
                    "Batch stopped",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowError("Batch failed", ex);
        }
        finally
        {
            _stop?.Dispose();
            _stop = null;
            IsBusy = false;
        }
    }

    private void RefreshRows()
    {
        if (_store is null || _job is null)
        {
            return;
        }

        var byId = _store.GetCandidates(_job.JobId).ToDictionary(c => c.CandidateId, StringComparer.Ordinal);

        foreach (var row in Rows)
        {
            if (byId.TryGetValue(row.Candidate.CandidateId, out var updated))
            {
                row.Update(updated);
            }
        }

        RaiseTotals();
    }

    private async Task OpenInQuickBooksAsync()
    {
        if (_query is null || SelectedRow is null)
        {
            return;
        }

        var row = SelectedRow;

        try
        {
            var status = await _worker.RunAsync(
                () => _query.DisplayInQuickBooks(row.Candidate.TxnType, row.Candidate.TxnId));

            StatusText = status.IsOk
                ? $"Opened {row.Candidate.TxnId} in QuickBooks."
                : $"QuickBooks could not open the transaction: {status}";
        }
        catch (Exception ex)
        {
            ShowError("Could not open the transaction in QuickBooks", ex);
        }
    }

    private void Export()
    {
        if (_export is null || _job is null)
        {
            return;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "QbReclass");

        Directory.CreateDirectory(folder);

        var stem = Path.Combine(folder, $"{_job.JobId}-{DateTime.Now:yyyyMMdd-HHmmss}");

        try
        {
            _export.ExportCsv(_job.JobId, stem + ".csv");
            _export.ExportJson(_job.JobId, stem + ".json");

            StatusText = $"Audit exported to {folder}.";
            MessageBox.Show(
                $"Audit log written to:{Environment.NewLine}{stem}.csv{Environment.NewLine}{stem}.json"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "These files contain financial data. Store them accordingly.",
                "Audit exported",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("Export failed", ex);
        }
    }

    private Job BuildJob()
    {
        var types = new List<TransactionType>();

        if (IncludeCreditCardCharges)
        {
            types.Add(TransactionType.CreditCardCharge);
        }

        if (IncludeChecks)
        {
            types.Add(TransactionType.Check);
        }

        if (IncludeBills)
        {
            types.Add(TransactionType.Bill);
        }

        return new Job
        {
            JobId = $"JOB-{DateTime.Now:yyyyMMdd-HHmmss}",
            Company = _session!.Info.Company,
            FromDate = DateOnly.FromDateTime(FromDate),
            ToDate = DateOnly.FromDateTime(ToDate),
            SourceAccount = SourceAccount!,
            DestinationAccount = DestinationAccount!,
            TransactionTypes = types,
            BatchSize = BatchSize,
            BackupConfirmedAt = BackupConfirmedAt,
            StopPolicy = ContinueOnIsolatedFailure
                ? StopPolicy.ContinueOnIsolatedFailure
                : StopPolicy.StopOnFirstFailure,
            Filters = new JobFilters
            {
                PayeeContains = Nullify(PayeeFilter),
                MemoContains = Nullify(MemoFilter),
                RefNumberContains = Nullify(ReferenceFilter),
                MinAmount = decimal.TryParse(MinAmount, out var min) ? min : null,
                MaxAmount = decimal.TryParse(MaxAmount, out var max) ? max : null,
            },
        };
    }

    /// <summary>Picks a .QBW file to connect against.</summary>
    private void BrowseCompanyFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a QuickBooks company file",
            Filter = "QuickBooks company files (*.QBW)|*.QBW|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            CompanyFilePath = dialog.FileName;
        }
    }

    private static string? Nullify(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RaiseTotals()
    {
        Raise(nameof(MatchCount));
        Raise(nameof(MatchTotal));
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectedTotal));
        Raise(nameof(UnsupportedCount));
        Raise(nameof(TotalsText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        ConnectSimulatedCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        SelectNoneCommand.RaiseCanExecuteChanged();
        RunBatchCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        OpenInQuickBooksCommand.RaiseCanExecuteChanged();
        BrowseCompanyFileCommand.RaiseCanExecuteChanged();
    }

    private void ShowError(string title, Exception ex)
    {
        StatusText = $"{title}: {ex.Message}";
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _session?.Dispose();
        _store?.Dispose();
        _worker.Dispose();
    }
}

/// <summary>A small demonstration company, so the interface can be exercised without QuickBooks.</summary>
internal static class DemoCompany
{
    public static SimulatedCompany Build()
    {
        var company = new SimulatedCompany("Demo Bookkeeping LLC");

        var card = company.AddAccount("Business Visa", "CreditCard");
        var bank = company.AddAccount("Operating Checking", "Bank");
        var askMyAccountant = company.AddAccount("Ask My Accountant", "Expense");
        company.AddAccount("Automobile:Fuel", "Expense");
        var meals = company.AddAccount("Meals and Entertainment", "Expense");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(DateTime.Today.Year, 2, 3), "Shell",
            [(askMyAccountant, 54.10m, "fuel")], refNumber: "C-1001");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(DateTime.Today.Year, 3, 11), "Costco",
            [(askMyAccountant, 88.00m, "fuel"), (meals, 31.75m, "lunch")], refNumber: "C-1002");

        company.AddTransaction(
            TransactionType.CreditCardCharge, card, new DateOnly(DateTime.Today.Year, 4, 18), "Chevron",
            [(askMyAccountant, 63.90m, "fuel")], refNumber: "C-1003", cleared: ClearedStatus.Reconciled);

        company.AddTransaction(
            TransactionType.Check, bank, new DateOnly(DateTime.Today.Year, 6, 7), "Valley Fuel Co",
            [(askMyAccountant, 412.00m, "bulk diesel")], refNumber: "2210");

        return company;
    }
}
