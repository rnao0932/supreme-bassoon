using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QbReclass.Core.Model;

namespace QbReclass.App;

/// <summary>
/// The batch approval screen (spec section 9).
/// </summary>
/// <remarks>
/// Every item the spec lists is rendered from <see cref="BatchApprovalSummary"/> rather than laid
/// out by hand, so a field cannot be dropped from the screen without being dropped from the model.
/// The confirming button is labelled with the action and the count - never "OK" or "Continue".
/// </remarks>
public partial class BatchApprovalWindow : Window
{
    public BatchApprovalWindow(BatchApprovalSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        InitializeComponent();

        ApplyButton.Content = summary.ButtonText;
        ScopeText.Text = summary.ScopeStatement;

        AddRow("Company file", summary.Company.CompanyName);
        AddRow(string.Empty, summary.Company.CompanyFileName);
        AddRow("Job rule", $"{summary.SourceAccount}  →  {summary.DestinationAccount}");
        AddRow("Date range", $"{summary.FromDate:yyyy-MM-dd} to {summary.ToDate:yyyy-MM-dd}");
        AddRow("Transaction types", string.Join(", ", summary.TransactionTypes));
        AddRow("Transactions", summary.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));
        AddRow("Transaction lines", summary.LineCount.ToString("N0", CultureInfo.CurrentCulture));
        AddRow("Total amount", summary.TotalAmount.ToString("N2", CultureInfo.CurrentCulture));

        AddRow(
            "Reconciled or cleared",
            summary.ReconciledCount == 0
                ? "0 reported — see the preview notice if QuickBooks did not report this"
                : summary.ReconciledCount.ToString("N0", CultureInfo.CurrentCulture),
            highlight: summary.ReconciledCount > 0);

        AddRow(
            "Warnings",
            summary.WarningCount.ToString("N0", CultureInfo.CurrentCulture),
            highlight: summary.WarningCount > 0);

        AddRow(
            "Unsupported records",
            summary.UnsupportedCount.ToString("N0", CultureInfo.CurrentCulture),
            highlight: summary.UnsupportedCount > 0);

        AddRow(
            "Backup confirmed",
            summary.BackupConfirmedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            ?? "NOT CONFIRMED",
            highlight: summary.BackupConfirmedAt is null);
    }

    private void AddRow(string label, string value, bool highlight = false)
    {
        var row = DetailGrid.RowDefinitions.Count;
        DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 12, 3),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        DetailGrid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            Margin = new Thickness(0, 3, 0, 3),
            TextWrapping = TextWrapping.Wrap,
            Foreground = highlight ? Brushes.DarkRed : SystemColors.ControlTextBrush,
            FontWeight = highlight ? FontWeights.SemiBold : FontWeights.Normal,
        };

        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);
        DetailGrid.Children.Add(valueBlock);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
