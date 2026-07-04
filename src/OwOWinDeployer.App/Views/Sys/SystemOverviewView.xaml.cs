using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Sys;

public partial class SystemOverviewView : UserControl
{
    public SystemOverviewView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as SystemOverviewViewModel)?.StartLive();
        Unloaded += (_, _) => (DataContext as SystemOverviewViewModel)?.StopLive();
    }
}
