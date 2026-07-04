using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Server;

public partial class ServiceConfigView : UserControl
{
    public ServiceConfigView()
    {
        InitializeComponent();
        // Re-validate on every navigation in: if a previously-opened server was uninstalled meanwhile,
        // drop back to the server list instead of staying on the now-stale detail page.
        Loaded += (_, _) => (DataContext as ServiceConfigViewModel)?.Activate();
    }
}
