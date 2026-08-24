using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FourthIBTracker.Services;
using FourthIBTracker.ViewModels;
using FourthIBTracker.Views;
using Microsoft.Web.WebView2.Wpf;

namespace FourthIBTracker;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private GoogleSheetsService _sheets;
    private readonly TodoViewModel _todoViewModel;
    private readonly UpdateViewModel _updateViewModel;

    // Views are created once and cached so their state (and WebView2 sessions) survive navigation.
    private DashboardView? _dashboard;
    private AttendanceView? _attendance;
    private CoursesView? _courses;
    private CampaignMedalsView? _campaignMedals;
    private CefoView? _cefo;
    private FillInsView? _fillIns;
    private LinksView? _links;
    private ForumCoursesView? _forumCourses;
    private AddressView? _address;
    private TodoView? _todo;
    private PatrolReportsView? _patrolReports;
    private TrainingReportsView? _trainingReports;
    private readonly Dictionary<string, WebView2> _browsers = new();

    public MainWindow()
    {
        InitializeComponent();
        _config = AppConfig.Load();
        _sheets = new GoogleSheetsService(_config);
        _todoViewModel = new TodoViewModel();
        _updateViewModel = new UpdateViewModel(new UpdateService())
        {
            ConfirmInstall = ConfirmSettingsApply,
            ShutdownApplication = () => Application.Current.Shutdown(),
        };
        TodoNavButton.DataContext = _todoViewModel;
        UpdateButton.DataContext = _updateViewModel;
        BrowserTabList.ItemsSource = _config.BrowserTabs;
        Title = $"4thIB Tracker — {_config.Platoon.Name}";
        TitleBarSubtitle.Text = $"·  {_config.Platoon.Name}";
        FooterText.Text = $"v{_updateViewModel.CurrentVersion}";
        ContentRendered += CheckForUpdatesAfterStartup;
        NavDashboard_Click(this, new RoutedEventArgs());
    }

    private async void CheckForUpdatesAfterStartup(object? sender, EventArgs e)
    {
        ContentRendered -= CheckForUpdatesAfterStartup;
        await Task.Delay(1500);
        await _updateViewModel.CheckAsync(silent: true);
    }

    // ------- custom title bar buttons -------
    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------- correct maximise bounds for a WindowStyle=None window -------
    // Without this, maximising covers the whole screen (including the taskbar)
    // and the bottom of every page gets cut off.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                // Maximised size = the monitor's work area (excludes the taskbar).
                mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTAPI { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTAPI { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINTAPI ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECTAPI rcMonitor, rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void NavDashboard_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _dashboard ??= new DashboardView(new DashboardViewModel(_sheets, _config));

    private void NavAttendance_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _attendance ??= new AttendanceView(new AttendanceViewModel(_sheets, _config));

    private void NavCourses_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _courses ??= new CoursesView(new CoursesViewModel(_sheets, _config));

    private void NavCampaignMedals_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _campaignMedals ??=
            new CampaignMedalsView(new CampaignMedalsViewModel(_sheets, _config));

    private void NavCefo_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _cefo ??= new CefoView(new CefoViewModel(_sheets, _config));

    private void NavFillIns_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _fillIns ??= new FillInsView(new FillInsViewModel(_sheets, _config));

    private void NavLinks_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _links ??= new LinksView(new LinksViewModel(_sheets, _config));

    private void NavForumCourses_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _forumCourses ??= new ForumCoursesView(new ForumCoursesViewModel(_sheets, _config));

    private void NavAddress_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _address ??= new AddressView(new AddressViewModel(_sheets, _config));

    private void NavTodo_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _todo ??= new TodoView(_todoViewModel);

    private void NavPatrolReports_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _patrolReports ??= new PatrolReportsView(new PatrolReportsViewModel(_config));

    private void NavTrainingReports_Click(object sender, RoutedEventArgs e) =>
        ContentHost.Content = _trainingReports ??= new TrainingReportsView(new TrainingReportsViewModel(_config));

    private SettingsView? _settings;
    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            var vm = new SettingsViewModel(_config, _updateViewModel)
            {
                ConfirmApply = ConfirmSettingsApply,
            };
            vm.SettingsSaved += ApplySettingsWithoutRestart;
            _settings = new SettingsView(vm);
        }
        ContentHost.Content = _settings;
    }

    private void NavUpdate_Click(object sender, RoutedEventArgs e) =>
        NavSettings_Click(sender, e);

    private bool ConfirmSettingsApply()
    {
        var unsaved = new List<string>();
        if (_attendance?.DataContext is AttendanceViewModel attendance && attendance.DirtyCount > 0)
            unsaved.Add($"{attendance.DirtyCount} attendance change(s)");
        if (_fillIns?.DataContext is FillInsViewModel fillIns &&
            fillIns.Pending.Any(p => !p.IsSubmitted))
            unsaved.Add($"{fillIns.Pending.Count(p => !p.IsSubmitted)} queued fill-in(s)");

        if (unsaved.Count == 0) return true;
        return MessageBox.Show(
            "Applying settings reloads the affected pages and will discard:\n\n" +
            string.Join("\n", unsaved.Select(s => $"• {s}")) +
            "\n\nSave settings anyway?",
            "Apply settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void ApplySettingsWithoutRestart()
    {
        Title = $"4thIB Tracker — {_config.Platoon.Name}";
        TitleBarSubtitle.Text = $"·  {_config.Platoon.Name}";

        // Browser-tab ItemsSource points at the old List instance after Save,
        // so explicitly rebind it and discard views whose URL may have changed.
        BrowserTabList.ItemsSource = null;
        BrowserTabList.ItemsSource = _config.BrowserTabs;
        foreach (var browser in _browsers.Values) browser.Dispose();
        _browsers.Clear();

        // Recreate the service and every config-backed page on next navigation.
        // This is the in-process equivalent of the old restart workflow.
        _sheets = new GoogleSheetsService(_config);
        _dashboard = null;
        _attendance = null;
        _courses = null;
        _campaignMedals = null;
        _cefo = null;
        _fillIns = null;
        _links = null;
        _forumCourses = null;
        _address = null;
        _patrolReports = null;
        _trainingReports = null;
    }

    private async void NavBrowserTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not BrowserTab tab) return;
        if (!_browsers.TryGetValue(tab.Name, out var view))
        {
            view = new WebView2();
            _browsers[tab.Name] = view;
            var env = await WebViewEnvironmentService.GetAsync();
            await view.EnsureCoreWebView2Async(env);
            view.CoreWebView2.Navigate(tab.Url);
        }
        ContentHost.Content = view;
    }
}
