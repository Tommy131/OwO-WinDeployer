using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Deploy;

public partial class DetailView : UserControl
{
    public DetailView()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as DetailViewModel)?.StartRunningWatch();
        Unloaded += (_, _) => (DataContext as DetailViewModel)?.StopRunningWatch();
    }
}
