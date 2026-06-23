using System.ComponentModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.App.ViewModels;
using WinDeploy.App.Views;

namespace WinDeploy.App;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private bool _exiting;   // set when a real shutdown is in progress

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        // Window / taskbar icon comes from the embedded ApplicationIcon (app.ico) — same as the .exe file icon.
        // Native title bar gets its handle only at SourceInitialized; theme it then.
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            (DataContext as MainViewModel)?.Terminal.Dispose();
            (DataContext as MainViewModel)?.Ftp.Shutdown();
        };
    }

    /// <summary>Close-button behavior: ask (default) → prompt; tray → minimize to tray; exit → really quit.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;   // exit already chosen — let it close

        var action = SettingsStore.Load().CloseAction ?? "ask";

        if (action == "exit") { _exiting = true; return; }
        if (action == "tray") { e.Cancel = true; HideToTray(); return; }

        // ask
        var dlg = new CloseChoiceDialog { Owner = this };
        var ok = dlg.ShowDialog();
        if (ok != true || dlg.Choice == CloseChoice.Cancel) { e.Cancel = true; return; }

        if (dlg.Remember) SettingsStore.SetCloseAction(dlg.Choice == CloseChoice.Tray ? "tray" : "exit");

        if (dlg.Choice == CloseChoice.Tray) { e.Cancel = true; HideToTray(); }
        else _exiting = true;   // CloseChoice.Exit → allow close
    }

    private void HideToTray()
    {
        _tray ??= new TrayIcon(WinDeploy.App.AppInfo.TitleWithVersion, RestoreFromTray, ExitFromTray);
        _tray.Show();
        Hide();
        AuditLog.App("已最小化到后台常驻（系统托盘）");
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // nudge to foreground
        _tray?.Hide();
    }

    private void ExitFromTray()
    {
        _exiting = true;
        _tray?.Dispose();
        Application.Current.Shutdown();
    }
}
