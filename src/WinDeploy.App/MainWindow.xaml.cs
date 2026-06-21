using System.Windows;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
