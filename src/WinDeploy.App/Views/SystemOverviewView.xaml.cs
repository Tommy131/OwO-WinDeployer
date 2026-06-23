using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class SystemOverviewView : UserControl
{
    public SystemOverviewView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as SystemOverviewViewModel)?.StartLive();
        Unloaded += (_, _) => (DataContext as SystemOverviewViewModel)?.StopLive();
    }
}
