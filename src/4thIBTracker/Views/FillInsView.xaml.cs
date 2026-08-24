using System.Windows.Controls;
using System.Windows.Input;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class FillInsView : UserControl
{
    private readonly FillInsViewModel _vm;

    public FillInsView(FillInsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (!_vm.HasData && !_vm.IsLoading)
                await _vm.LoadAsync();
        };
    }

    private void Roster_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedSoldier != null)
            _vm.AddCommand.Execute(null);
    }
}
