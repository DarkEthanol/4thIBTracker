using System.Windows.Controls;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class CampaignMedalsView : UserControl
{
    public CampaignMedalsView(CampaignMedalsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            if (!vm.HasLoaded && !vm.IsLoading)
                await vm.LoadAsync();
        };
    }
}
