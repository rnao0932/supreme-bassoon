using QbReclass.Core.Model;

namespace QbReclass.App;

/// <summary>
/// One row of the preview grid. Wraps a <see cref="CandidateLine"/> with the selection state and
/// the display strings the grid binds to (spec section 8).
/// </summary>
public sealed class CandidateRow : ObservableObject
{
    private bool _isSelected;

    public CandidateRow(CandidateLine candidate)
    {
        Candidate = candidate;
        _isSelected = candidate.IsSelected;
    }

    public CandidateLine Candidate { get; private set; }

    /// <summary>
    /// A row that cannot be written cannot be selected, so the checkbox is not merely ignored - it
    /// refuses to turn on (spec FR-018).
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var effective = value && Candidate.IsSelectable;
            if (Set(ref _isSelected, effective))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            else if (value != effective)
            {
                // Snap the checkbox back so the user sees the refusal.
                Raise(nameof(IsSelected));
            }
        }
    }

    public event EventHandler? SelectionChanged;

    public bool CanSelect => Candidate.IsSelectable;

    public string Type => Candidate.TxnType switch
    {
        TransactionType.CreditCardCharge => "Credit Card Charge",
        TransactionType.CreditCardCredit => "Credit Card Credit",
        TransactionType.Check => "Check",
        _ => Candidate.TxnType.ToString(),
    };

    public string Date => Candidate.TxnDate.ToString("yyyy-MM-dd");

    public string? Payee => Candidate.PayeeName;

    public string? Reference => Candidate.RefNumber;

    public decimal Amount => Candidate.LineAmount;

    public string CurrentAccount => Candidate.CurrentAccount.ToString();

    public string ProposedAccount => Candidate.ProposedAccount.ToString();

    public string? Memo => Candidate.Memo;

    public string? Class => Candidate.ClassName;

    /// <summary>Shows "?" rather than "No" when QuickBooks did not report a cleared status.</summary>
    public string Reconciled => Candidate.Cleared switch
    {
        ClearedStatus.Reconciled => "Reconciled",
        ClearedStatus.Cleared => "Cleared",
        ClearedStatus.NotCleared => "No",
        _ => "?",
    };

    public string Status => Candidate.StatusLabel;

    public bool HasWarnings => Candidate.Warnings.Count > 0;

    public string WarningText => string.Join(Environment.NewLine, Candidate.Warnings.Select(w => "- " + w.Message));

    /// <summary>Content of the detail pane shown when a row is clicked (spec section 8).</summary>
    public string Detail
    {
        get
        {
            var lines = new List<string>
            {
                $"Transaction type:   {Type}",
                $"TxnID:              {Candidate.TxnId}",
                $"TxnLineID:          {Candidate.TxnLineId}  (line {Candidate.LineOrdinal + 1})",
                $"EditSequence:       {Candidate.EditSequenceAtPreview}",
                $"Date:               {Date}",
                $"Payee:              {Payee ?? "(none)"}",
                $"Reference:          {Reference ?? "(none)"}",
                $"Posting account:    {Candidate.PostingAccountName ?? "(not reported)"}",
                $"Line amount:        {Amount:N2}",
                $"Transaction total:  {Candidate.TxnTotalAmount:N2}",
                $"Memo:               {Memo ?? "(none)"}",
                $"Class:              {Class ?? "(none)"}",
                $"Reconciled:         {Reconciled}",
                string.Empty,
                $"Current account:    {CurrentAccount}",
                $"Proposed account:   {ProposedAccount}",
                string.Empty,
                $"Status:             {Status}",
            };

            if (Candidate.UnsupportedReason is { } reason)
            {
                lines.Add(string.Empty);
                lines.Add("Cannot be changed by this utility:");
                lines.Add("  " + reason);
            }

            if (HasWarnings)
            {
                lines.Add(string.Empty);
                lines.Add("Warnings:");
                lines.Add(WarningText);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Applies a refreshed candidate after execution, so the grid shows the new status.</summary>
    public void Update(CandidateLine candidate)
    {
        Candidate = candidate;
        _isSelected = candidate.IsSelected;

        Raise(nameof(IsSelected));
        Raise(nameof(Status));
        Raise(nameof(CurrentAccount));
        Raise(nameof(Detail));
        Raise(nameof(CanSelect));
    }
}
