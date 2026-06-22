using System.Windows.Controls;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            (DataContext as TerminalViewModel)?.EnsureStarted();
            InBox.Focus();
        };
    }

    private void OutBox_TextChanged(object sender, TextChangedEventArgs e)
        => ((TextBox)sender).ScrollToEnd();
}
