using System.Windows;
using System.Windows.Threading;

namespace QbReclass.App;

/// <summary>Application entry point.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception must not leave the user guessing whether a write completed.
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:{Environment.NewLine}{Environment.NewLine}{e.Exception.Message}"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "Any batch in progress has stopped. Reconnect to reconcile work that was in flight before "
            + "starting another batch.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
