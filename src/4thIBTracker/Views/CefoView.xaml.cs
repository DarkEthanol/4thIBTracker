using System.Windows.Controls;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class CefoView : UserControl
{
    public CefoView(CefoViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (vm.Groups.Count == 0 && !vm.IsLoading)
                await vm.LoadAsync();
        };
    }
}
