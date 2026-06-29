using System.Windows.Controls;

namespace WinDeploy.App.Views.Clip;

public partial class ClipPeersView : UserControl
{
    public ClipPeersView()
    {
        InitializeComponent();
        // Keep the activity log scrolled to the newest line.
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }
}
