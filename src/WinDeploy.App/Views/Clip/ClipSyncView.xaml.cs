using System.Windows.Controls;
using WinDeploy.App.ViewModels.Clip;

namespace WinDeploy.App.Views.Clip;

public partial class ClipSyncView : UserControl
{
    public ClipSyncView()
    {
        InitializeComponent();
        // Keep the live status / "since" timers running only while this page is visible (the share itself
        // keeps running in the background even when the page is hidden).
        Loaded += (_, _) => (DataContext as ClipSyncViewModel)?.Activate();
        Unloaded += (_, _) => (DataContext as ClipSyncViewModel)?.Deactivate();
    }
}
