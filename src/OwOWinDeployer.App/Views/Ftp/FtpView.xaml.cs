using System.Windows.Controls;
using OwOWinDeployer.App.ViewModels;

namespace OwOWinDeployer.App.Views.Ftp;

public partial class FtpView : UserControl
{
    public FtpView()
    {
        InitializeComponent();
        // Keep the server status/connection table refreshing only while this page is visible.
        Loaded += (_, _) => (DataContext as FtpViewModel)?.Activate();
        Unloaded += (_, _) => (DataContext as FtpViewModel)?.Deactivate();
    }
}
