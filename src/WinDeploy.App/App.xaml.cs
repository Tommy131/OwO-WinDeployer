using System.Windows;
using WinDeploy.App.Services;

namespace WinDeploy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Set a stable AppUserModelID first, so tray balloons (shown as toasts on Win10/11) are attributed to
        // "OwO! Win Deployer" rather than an auto-generated "NotifyIconGeneratedAumid_…" id.
        AppUserModel.Configure();
        var settings = SettingsStore.Load();
        ThemeManager.Apply(ThemeManager.Parse(settings.Theme));
        AuditLog.App($"应用启动 · 主题 {settings.Theme ?? "system"}");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AuditLog.App("应用退出");
        base.OnExit(e);
    }
}
