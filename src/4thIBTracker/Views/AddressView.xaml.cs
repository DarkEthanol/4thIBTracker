using System.Windows.Controls;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class AddressView : UserControl
{
    public AddressView(AddressViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (!vm.HasData && !vm.IsLoading)
                await vm.LoadAsync();
        };
    }
}
