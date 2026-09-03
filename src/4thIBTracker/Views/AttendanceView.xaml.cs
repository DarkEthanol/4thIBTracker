using System.Windows.Controls;
using System.Windows.Input;
using FourthIBTracker.Models;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class AttendanceView : UserControl
{
    private readonly AttendanceViewModel _vm;

    public AttendanceView(
        AttendanceViewModel vm,
        PlatoonAttendanceViewModel websiteAttendanceViewModel)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        WebsiteAttendanceHost.Content = new PlatoonAttendanceView(websiteAttendanceViewModel);
        Focusable = true;
        Loaded += async (_, _) =>
        {
            Focus();
            if (vm.Sections.Count == 0 && !vm.IsLoading)
                await vm.LoadAsync();
        };
        MouseEnter += (_, _) => Focus();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Hotkeys: 1 Present · 2 LOA · 3 Late · 4 AWOL · 0 Clear.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Don't steal keystrokes from text inputs.
        if (Keyboard.FocusedElement is TextBox) return;

        var status = e.Key switch
        {
            Key.D1 or Key.NumPad1 => (AttendanceStatus?)AttendanceStatus.Present,
            Key.D2 or Key.NumPad2 => AttendanceStatus.Loa,
            Key.D3 or Key.NumPad3 => AttendanceStatus.Late,
            Key.D4 or Key.NumPad4 => AttendanceStatus.Awol,
            Key.D0 or Key.NumPad0 => AttendanceStatus.None,
            _ => null,
        };
        if (status != null)
        {
            _vm.SelectedStatus = status.Value;
            e.Handled = true;
        }
    }
}
