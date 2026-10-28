using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using FourthIBTracker.Services;

namespace FourthIBTracker;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\FourthIBTracker.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

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

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out _ownsSingleInstanceMutex);
        if (!_ownsSingleInstanceMutex)
        {
            ActivateExistingInstance();
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var existing = Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(process =>
                    process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero);
            if (existing is null) return;
            using (existing)
            {
                ShowWindow(existing.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(existing.MainWindowHandle);
            }
        }
        catch
        {
            // The mutex remains authoritative even if Windows refuses focus.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
