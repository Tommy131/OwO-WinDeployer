using System.Windows.Controls;
using System.Windows.Input;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App.Views;

public partial class FtpClientView : UserControl
{
    public FtpClientView()
    {
        InitializeComponent();
        // PasswordBox can't bind; push changes into the VM.
        PwBox.PasswordChanged += (_, _) => { if (DataContext is FtpClientViewModel vm) vm.Password = PwBox.Password; };
        LogBox.TextChanged += (_, _) => LogBox.ScrollToEnd();
    }

    private void LocalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FtpClientViewModel vm && LocalGrid.SelectedItem is FtpLocalRowVm row)
            vm.OpenLocalCommand.Execute(row);
    }

    private void RemoteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FtpClientViewModel vm && RemoteGrid.SelectedItem is FtpRemoteRowVm row)
            vm.OpenRemoteCommand.Execute(row);
    }
}
