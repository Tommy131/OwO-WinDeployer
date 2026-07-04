using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Sys;

public partial class ProcessManagerView : UserControl
{
    public ProcessManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ProcessManagerViewModel)?.StartLive();
        Unloaded += (_, _) => (DataContext as ProcessManagerViewModel)?.StopLive();
    }
}
