using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Sys;

public partial class StartupView : UserControl
{
    public StartupView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is StartupViewModel vm && vm.Items.Count == 0) vm.Refresh(); };
    }
}
