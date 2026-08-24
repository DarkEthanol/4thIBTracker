using System.Windows;
using FourthIBTracker.Services;

namespace FourthIBTracker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (UpdateService.IsApplyUpdateCommand(e.Args))
        {
            try
            {
                UpdateService.ApplyUpdateAndRestart(e.Args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "The update could not be applied. The existing app will be reopened.\n\n" + ex.Message,
                    "4thIB Tracker — Update failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                UpdateService.TryRestartOriginal(e.Args);
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        UpdateService.SchedulePostUpdateCleanup(e.Args);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "4thIB Tracker — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            // If we crashed before the main window ever appeared (e.g. bad
            // configuration), don't leave an invisible process running.
            if (MainWindow == null || !MainWindow.IsLoaded) Shutdown(1);
        };
    }
}
