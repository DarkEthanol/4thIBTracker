using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void PrivacyLink_RequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        e.Handled = true;
    }
}
