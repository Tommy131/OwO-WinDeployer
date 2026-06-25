using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core.I18n;

namespace WinDeploy.App;

public partial class App : Application
{
    // Single-instance guard. Names live in the per-session (Local) namespace, so the app is limited to one
    // instance per logged-in user. The mutex detects a running instance; the event lets a second launch ask
    // the first one to surface its window (important since it can be minimized to the tray).
    private const string InstanceMutexName = @"Local\OwO.WinDeployer.SingleInstance.{8F3A6C21-4D7E-4B9A-9C2F-1E5A7D0B3F44}";
    private const string ActivateEventName = @"Local\OwO.WinDeployer.Activate.{8F3A6C21-4D7E-4B9A-9C2F-1E5A7D0B3F44}";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private bool _isPrimary;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only the first instance proceeds. A duplicate launch signals the running instance to come to the
        // foreground, then shuts itself down before creating any window.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _isPrimary);
        if (!_isPrimary)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
            catch { /* primary may be exiting; nothing to surface */ }
            Shutdown();
            return;
        }

        // Catch unhandled exceptions so a single page/feature fault logs an error and keeps the app alive,
        // instead of crashing the whole window to the desktop. The full stack is written to crash.log.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash("AppDomain", ex.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) => { LogCrash("Task", ex.Exception); ex.SetObserved(); };

        // Set a stable AppUserModelID first, so tray balloons (shown as toasts on Win10/11) are attributed to
        // "OwO! Win Deployer" rather than an auto-generated "NotifyIconGeneratedAumid_…" id.
        AppUserModel.Configure();
        var settings = SettingsStore.Load();

        // Language: saved choice wins; on first run follow the Windows UI language (de/zh, else en),
        // then persist it. Seed the S.* string resources BEFORE base.OnStartup creates MainWindow,
        // so every {DynamicResource S.*} resolves on first layout.
        var lang = settings.Language ?? Lang.FromCulture(CultureInfo.CurrentUICulture);
        if (settings.Language is null) { settings.Language = lang; SettingsStore.Save(settings); }
        Localizer.SetLanguage(lang);
        LocalizationManager.Apply();

        ThemeManager.Apply(ThemeManager.Parse(settings.Theme));

        // Route downloads through the saved proxy (no-op / system default when disabled).
        WinDeploy.Core.Net.HttpProxy.Apply(settings.ProxyEnabled, settings.ProxyUrl);

        // Start the background hardware-temperature watchdog (no-op unless enabled in settings).
        TempMonitor.Configure(TempMonitorConfig.From(settings));

        AuditLog.App($"应用启动 · 语言 {lang} · 主题 {settings.Theme ?? "system"}");
        base.OnStartup(e);

        // Listen for later launches asking us to surface, then create the main window ourselves
        // (App.xaml has no StartupUri — see the comment there).
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        StartActivationListener();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    // Background thread that wakes whenever another instance signals the activation event, then asks the
    // main window to come to the foreground on the UI thread.
    private void StartActivationListener()
    {
        var thread = new Thread(() =>
        {
            var handle = _activateEvent;
            while (handle is not null)
            {
                try { handle.WaitOne(); }
                catch { break; }
                try { Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.SurfaceFromOtherInstance()); }
                catch { /* shutting down */ }
            }
        })
        { IsBackground = true, Name = "SingleInstanceActivate" };
        thread.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI", e.Exception);
        MessageBox.Show(
            Localizer.Format("crash.dialogBody", e.Exception.Message, e.Exception.GetType().FullName),
            Localizer.T("crash.dialogTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;   // keep the app alive
    }

    private static void LogCrash(string source, Exception? ex)
    {
        if (ex == null) return;
        try { AuditLog.App($"未处理异常[{source}]：{ex.GetType().Name}: {ex.Message}"); } catch { }
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(Path.Combine(SettingsStore.Folder, "crash.log"), $"==== {DateTime.Now:O} [{source}] ====\n{ex}\n\n");
        }
        catch { /* best effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_isPrimary)
        {
            TempMonitor.Stop();
            AuditLog.App("应用退出");
            try { _instanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        }
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
