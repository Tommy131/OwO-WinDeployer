using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        // Native title bar gets its handle only at SourceInitialized; theme it then.
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) => (DataContext as MainViewModel)?.Terminal.Dispose();
    }
}
