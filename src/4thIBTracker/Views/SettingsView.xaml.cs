using System.Windows.Controls;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
