using System.ComponentModel;
using System.Windows;

namespace QbReclass.App;

/// <summary>Shell window. All behaviour lives in <see cref="MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            var answer = MessageBox.Show(
                "A batch is still running. Closing now stops it after the transaction in progress; "
                + "anything already written stays written and will be reconciled next time you connect."
                + Environment.NewLine + Environment.NewLine
                + "Close anyway?",
                "Batch in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
        _viewModel.Dispose();
    }
}
