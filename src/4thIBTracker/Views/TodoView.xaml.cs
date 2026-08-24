using System.Windows.Controls;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class TodoView : UserControl
{
    public TodoView(TodoViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Recompute due dates whenever the view is shown (day may have rolled over).
        Loaded += (_, _) => vm.Refresh();
    }
}
