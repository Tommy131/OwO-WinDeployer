using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OwOWinDeployer.App.Services;
using OwOWinDeployer.Core.I18n;
using OwOWinDeployer.Core.Net;

namespace OwOWinDeployer.App.ViewModels.Shell;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;

    public SettingsViewModel()
    {
        _s = SettingsStore.Load();
        _lang = _s.Language ?? Localizer.Current;
        _devRoot = _s.DevRoot ?? "%USERPROFILE%/dev";
        _toolsDir = _s.ToolsDir ?? "%LOCALAPPDATA%/tools";
        _downloadDir = _s.DownloadDir ?? "%USERPROFILE%/Downloads/OwOWinDeployer";
        _repoUrl = _s.RepoUrl ?? "https://github.com/Tommy131/owo-win-deployer.git";
        _mirror = _s.Mirror ?? "";
        _redactKeywords = _s.RedactKeywords ?? "";
        _theme = _s.Theme ?? "system";
        _closeAction = _s.CloseAction ?? "ask";
        _developerMode = _s.DeveloperMode;
        _restorePointBeforeApply = _s.RestorePointBeforeApply;
        _runAtStartup = Services.Sys.AutoStart.IsEnabled();
        _alwaysShowTray = _s.AlwaysShowTray;
        _showLauncherOnStartup = _s.ShowLauncherOnStartup;
        _showDesktopWidget = _s.ShowDesktopWidget;
        _widgetOpacity = _s.WidgetOpacity;
        _widgetNativeGlass = _s.WidgetNativeGlass;
        _showAudioWidget = _s.ShowAudioWidget;
        _audioWidgetOpacity = _s.AudioWidgetOpacity;
        _audioWidgetNativeGlass = _s.AudioWidgetNativeGlass;
        _audioStyleIndex = (int)AudioWaveOptions.ParseStyle(_s.AudioWidgetStyle);
        _audioColorIndex = (int)AudioWaveOptions.ParseColor(_s.AudioWidgetColor);
        _audioSensitivity = _s.AudioWidgetSensitivity;
        _audioBeatReactive = _s.AudioWidgetBeatReactive;
        _audioSilenceFade = _s.AudioWidgetSilenceFade;
        _audioReadout = _s.AudioWidgetReadout;
        _audioGlow = _s.AudioWidgetGlow;
        _audioHueDrift = _s.AudioWidgetHueDrift;
        _audioClickThrough = _s.AudioWidgetClickThrough;
        _tempMonitorEnabled = _s.TempMonitorEnabled;
        _tempTts = _s.TempTtsEnabled;
        _tempCpu = _s.TempCpuEnabled; _tempGpu = _s.TempGpuEnabled; _tempDisk = _s.TempDiskEnabled;
        _cpuTempThreshold = _s.CpuTempThreshold; _gpuTempThreshold = _s.GpuTempThreshold; _diskTempThreshold = _s.DiskTempThreshold;
        _reminderSeconds = _s.TempReminderSeconds;
        _proxyEnabled = _s.ProxyEnabled;
        _proxyUrl = _s.ProxyUrl ?? "";
        SaveCommand = new RelayCommand(_ => Save());
        RefreshIconsCommand = new RelayCommand(_ => RefreshIconsRequested?.Invoke());
        TestTtsCommand = new RelayCommand(_ => Tts.Speak(Localizer.T("tempmon.tts.test")));
        SaveProxyCommand = new RelayCommand(_ => _ = SaveProxyAsync(), _ => !_proxyTesting);
        ResetCommand = new RelayCommand(_ => ResetAll());
    }

    public RelayCommand RefreshIconsCommand { get; }

    /// <summary>Raised when the user clicks 联网刷新软件图标; handled by MainViewModel (has the catalog).</summary>
    public event Action? RefreshIconsRequested;

    private string _iconNote = "";
    public string IconNote { get => _iconNote; set => Set(ref _iconNote, value); }

    private string _devRoot;
    public string DevRoot { get => _devRoot; set { if (Set(ref _devRoot, value)) Note = ""; } }

    private string _toolsDir;
    public string ToolsDir { get => _toolsDir; set { if (Set(ref _toolsDir, value)) Note = ""; } }

    private string _downloadDir;
    public string DownloadDir { get => _downloadDir; set { if (Set(ref _downloadDir, value)) Note = ""; } }

    private string _repoUrl;
    public string RepoUrl { get => _repoUrl; set { if (Set(ref _repoUrl, value)) Note = ""; } }

    private string _mirror;
    public string Mirror { get => _mirror; set { if (Set(ref _mirror, value)) Note = ""; } }

    private string _redactKeywords;
    public string RedactKeywords { get => _redactKeywords; set { if (Set(ref _redactKeywords, value)) Note = ""; } }

    // ── 下载代理（保存前必须通过连通性测试）─────────────────────────────────
    public RelayCommand SaveProxyCommand { get; }

    private bool _proxyEnabled;
    public bool ProxyEnabled { get => _proxyEnabled; set { if (Set(ref _proxyEnabled, value)) ProxyNote = ""; } }

    private string _proxyUrl;
    public string ProxyUrl { get => _proxyUrl; set { if (Set(ref _proxyUrl, value)) ProxyNote = ""; } }

    private bool _proxyTesting;
    public bool ProxyTesting { get => _proxyTesting; set { if (Set(ref _proxyTesting, value)) CommandManager.InvalidateRequerySuggested(); } }

    private string _proxyNote = "";
    public string ProxyNote { get => _proxyNote; set => Set(ref _proxyNote, value); }

    /// <summary>Persist + apply the download proxy. Disabling saves immediately; enabling first validates the
    /// format (strict regex) and verifies live connectivity through the proxy — saving only if reachable.</summary>
    private async Task SaveProxyAsync()
    {
        if (!_proxyEnabled)
        {
            _s.ProxyEnabled = false;
            _s.ProxyUrl = (_proxyUrl ?? "").Trim();
            SettingsStore.Save(_s);
            HttpProxy.Apply(false, _s.ProxyUrl);
            ProxyNote = Localizer.T("settings.proxy.disabled");
            AuditLog.Action("下载代理：关闭");
            return;
        }

        var url = (_proxyUrl ?? "").Trim();
        if (!HttpProxy.IsValid(url)) { ProxyNote = Localizer.T("settings.proxy.badFormat"); return; }

        ProxyTesting = true;
        ProxyNote = Localizer.T("settings.proxy.testing");
        var (ok, detail) = await HttpProxy.TestAsync(url);
        ProxyTesting = false;
        if (!ok)
        {
            ProxyNote = Localizer.Format("settings.proxy.unreachable", detail);   // not saved
            AuditLog.Action($"下载代理：连通性测试失败（{detail}），未保存");
            return;
        }

        _s.ProxyEnabled = true;
        _s.ProxyUrl = url;
        SettingsStore.Save(_s);
        HttpProxy.Apply(true, url);
        ProxyNote = Localizer.T("settings.proxy.ok");
        AuditLog.Action($"下载代理：已启用 {url}");
    }

    // ── 开发人员模式（即时生效并持久化）─────────────────────────────────
    private bool _developerMode;
    public bool DeveloperMode
    {
        get => _developerMode;
        set
        {
            if (value && !_developerMode)
            {
                // Turning on — require explicit confirmation; revert UI if user cancels.
                if (ConfirmEnableDeveloperMode?.Invoke() == false)
                {
                    OnPropertyChanged(nameof(DeveloperMode));   // revert checkbox
                    return;
                }
            }
            if (!Set(ref _developerMode, value)) return;
            _s.DeveloperMode = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"开发人员模式：{(value ? "开启（显示完整软件列表）" : "关闭（仅基础分类）")}");
            DeveloperModeChanged?.Invoke(value);
        }
    }

    /// <summary>勾选/取消开发人员模式时触发，让软件安装中心立即重算分类可见性。</summary>
    public event Action<bool>? DeveloperModeChanged;

    /// <summary>开启开发人员模式前触发，供外部显示二次确认弹窗。返回 false 则取消启用。</summary>
    public event Func<bool>? ConfirmEnableDeveloperMode;

    private bool _restorePointBeforeApply;
    /// <summary>批量安装前创建系统还原点（持久化；安装流程读取此设置）。</summary>
    public bool RestorePointBeforeApply
    {
        get => _restorePointBeforeApply;
        set { if (Set(ref _restorePointBeforeApply, value)) { _s.RestorePointBeforeApply = value; SettingsStore.Save(_s); } }
    }

    // ── 开机自启动 / 托盘常驻（即时生效并持久化）──────────────────────────
    private bool _runAtStartup;
    /// <summary>开机时自动启动：写入/删除当前用户的 Run 启动项（注册表为权威来源，无需管理员）。</summary>
    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (!Set(ref _runAtStartup, value)) return;
            var (ok, msg) = Services.Sys.AutoStart.Set(value);
            if (!ok)
            {
                _runAtStartup = !value;   // revert UI on failure
                OnPropertyChanged(nameof(RunAtStartup));
                Dialogs.Show(Localizer.Format("settings.autostart.fail", msg), Localizer.T("settings.title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AuditLog.Action($"开机自启动：{(value ? "开启" : "关闭")}");
        }
    }

    private bool _alwaysShowTray;
    /// <summary>始终在系统托盘显示常驻图标：无论窗口是否最小化都常驻一个托盘图标。即时生效。</summary>
    public bool AlwaysShowTray
    {
        get => _alwaysShowTray;
        set
        {
            if (!Set(ref _alwaysShowTray, value)) return;
            _s.AlwaysShowTray = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"托盘常驻图标：{(value ? "开启" : "关闭")}");
            AlwaysShowTrayChanged?.Invoke(value);
        }
    }

    /// <summary>切换「托盘常驻图标」时触发，让主窗口立即显示 / 隐藏常驻托盘图标。</summary>
    public event Action<bool>? AlwaysShowTrayChanged;

    private bool _showLauncherOnStartup;
    /// <summary>启动应用时自动打开「快速启动」页。仅影响下次启动的落地页；即时持久化。</summary>
    public bool ShowLauncherOnStartup
    {
        get => _showLauncherOnStartup;
        set
        {
            if (!Set(ref _showLauncherOnStartup, value)) return;
            _s.ShowLauncherOnStartup = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"启动时打开快速启动页：{(value ? "开启" : "关闭")}");
        }
    }

    private bool _showDesktopWidget;
    /// <summary>在桌面显示「快速启动」毛玻璃小组件。即时生效（显示/隐藏窗口）并持久化。</summary>
    public bool ShowDesktopWidget
    {
        get => _showDesktopWidget;
        set
        {
            if (!Set(ref _showDesktopWidget, value)) return;
            _s.ShowDesktopWidget = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"桌面快速启动小组件：{(value ? "开启" : "关闭")}");
            ShowDesktopWidgetChanged?.Invoke(value);
        }
    }

    /// <summary>切换「桌面小组件」时触发，让主窗口立即显示 / 隐藏桌面小组件。</summary>
    public event Action<bool>? ShowDesktopWidgetChanged;

    private double _widgetOpacity;
    /// <summary>小组件背景不透明度（0.1–0.85）。内部量：ApplyTint 用它算填充色 alpha。</summary>
    public double WidgetOpacity
    {
        get => _widgetOpacity;
        set
        {
            var v = Math.Clamp(Math.Round(value, 2), 0.1, 0.85);
            if (!Set(ref _widgetOpacity, v)) return;
            _s.WidgetOpacity = v;
            SettingsStore.Save(_s);
            OnPropertyChanged(nameof(WidgetSeeThrough));
            OnPropertyChanged(nameof(WidgetSeeThroughText));
            WidgetOpacityChanged?.Invoke(v);
        }
    }

    /// <summary>「透视程度」滑块绑定的值（0.15–0.9）：越大越透明。它就是 1 − 不透明度。</summary>
    public double WidgetSeeThrough
    {
        get => Math.Round(1 - _widgetOpacity, 2);
        set => WidgetOpacity = 1 - value;
    }
    public string WidgetSeeThroughText => $"{(1 - _widgetOpacity) * 100:0}%";

    /// <summary>调整小组件透视程度时触发，让桌面小组件实时更新背景。</summary>
    public event Action<double>? WidgetOpacityChanged;

    private bool _widgetNativeGlass;
    /// <summary>小组件毛玻璃：原生 DWM（即时、无圆角）vs WPF 截图模糊（平滑圆角）。即时生效（重建小组件）并持久化。</summary>
    public bool WidgetNativeGlass
    {
        get => _widgetNativeGlass;
        set
        {
            if (!Set(ref _widgetNativeGlass, value)) return;
            _s.WidgetNativeGlass = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"小组件毛玻璃模式：{(value ? "原生 DWM（无圆角）" : "WPF 截图模糊（圆角）")}");
            WidgetGlassModeChanged?.Invoke();
        }
    }

    /// <summary>切换小组件毛玻璃模式时触发，让主窗口按新模式重建桌面小组件。</summary>
    public event Action? WidgetGlassModeChanged;

    // ── 音频波形桌面组件（即时生效并持久化）────────────────────────────────
    private bool _showAudioWidget;
    /// <summary>在桌面显示「音频波形」毛玻璃小组件。即时生效（显示/隐藏窗口）并持久化。</summary>
    public bool ShowAudioWidget
    {
        get => _showAudioWidget;
        set
        {
            if (!Set(ref _showAudioWidget, value)) return;
            _s.ShowAudioWidget = value;
            SettingsStore.Save(_s);
            AuditLog.Action($"桌面音频波形小组件：{(value ? "开启" : "关闭")}");
            ShowAudioWidgetChanged?.Invoke(value);
        }
    }

    /// <summary>切换「音频波形组件」时触发，让主窗口立即显示 / 隐藏。</summary>
    public event Action<bool>? ShowAudioWidgetChanged;

    private double _audioWidgetOpacity;
    /// <summary>音频组件背景不透明度（0.1–0.85）。内部量：ApplyTint 用它算填充色 alpha。</summary>
    public double AudioWidgetOpacity
    {
        get => _audioWidgetOpacity;
        set
        {
            var v = Math.Clamp(Math.Round(value, 2), 0.1, 0.85);
            if (!Set(ref _audioWidgetOpacity, v)) return;
            _s.AudioWidgetOpacity = v;
            SettingsStore.Save(_s);
            OnPropertyChanged(nameof(AudioWidgetSeeThrough));
            OnPropertyChanged(nameof(AudioWidgetSeeThroughText));
            AudioWidgetOpacityChanged?.Invoke(v);
        }
    }

    /// <summary>「透视程度」滑块绑定的值（0.15–0.9）：越大越透明。它就是 1 − 不透明度。</summary>
    public double AudioWidgetSeeThrough
    {
        get => Math.Round(1 - _audioWidgetOpacity, 2);
        set => AudioWidgetOpacity = 1 - value;
    }
    public string AudioWidgetSeeThroughText => $"{(1 - _audioWidgetOpacity) * 100:0}%";

    /// <summary>调整音频组件透视程度时触发，让桌面组件实时更新背景。</summary>
    public event Action<double>? AudioWidgetOpacityChanged;

    private bool _audioWidgetNativeGlass;
    /// <summary>音频组件毛玻璃：原生 DWM（即时、无圆角）vs WPF 截图模糊（平滑圆角）。即时生效（重建组件）并持久化。</summary>
    public bool AudioWidgetNativeGlass
    {
        get => _audioWidgetNativeGlass;
        set
        {
            if (!Set(ref _audioWidgetNativeGlass, value)) return;
            _s.AudioWidgetNativeGlass = value;
            SettingsStore.Save(_s);
            AudioWidgetGlassModeChanged?.Invoke();
        }
    }

    /// <summary>切换音频组件毛玻璃模式时触发，让主窗口按新模式重建。</summary>
    public event Action? AudioWidgetGlassModeChanged;

    private int _audioStyleIndex;
    /// <summary>波形风格下拉框（0=镜像频谱 1=频谱柱 2=示波器 3=环形 4=填充丝带 5=声谱瀑布）。即时生效并持久化。</summary>
    public int AudioStyleIndex
    {
        get => _audioStyleIndex;
        set
        {
            var v = Math.Clamp(value, 0, AudioWaveOptions.StyleCount - 1);
            if (!Set(ref _audioStyleIndex, v)) return;
            _s.AudioWidgetStyle = AudioWaveOptions.ToToken((WaveStyle)v);
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private bool _audioBeatReactive;
    /// <summary>波形随节拍脉冲。即时生效并持久化。</summary>
    public bool AudioWidgetBeatReactive
    {
        get => _audioBeatReactive;
        set
        {
            if (!Set(ref _audioBeatReactive, value)) return;
            _s.AudioWidgetBeatReactive = value;
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private bool _audioSilenceFade;
    /// <summary>静音时波形淡出呼吸。即时生效并持久化。</summary>
    public bool AudioWidgetSilenceFade
    {
        get => _audioSilenceFade;
        set
        {
            if (!Set(ref _audioSilenceFade, value)) return;
            _s.AudioWidgetSilenceFade = value;
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private bool _audioReadout;
    /// <summary>组件角落显示实时读数（音名 · BPM · 电平）。即时生效并持久化。</summary>
    public bool AudioWidgetReadout
    {
        get => _audioReadout;
        set
        {
            if (!Set(ref _audioReadout, value)) return;
            _s.AudioWidgetReadout = value;
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private double _audioGlow;
    /// <summary>辉光强度（0.2–2.5）。即时生效并持久化。</summary>
    public double AudioWidgetGlow
    {
        get => _audioGlow;
        set
        {
            var v = Math.Clamp(Math.Round(value, 2), 0.2, 2.5);
            if (!Set(ref _audioGlow, v)) return;
            _s.AudioWidgetGlow = v;
            SettingsStore.Save(_s);
            OnPropertyChanged(nameof(AudioWidgetGlowText));
            AudioWidgetVisualChanged?.Invoke();
        }
    }
    public string AudioWidgetGlowText => $"{_audioGlow:0.0}×";

    private bool _audioHueDrift;
    /// <summary>色相随时间流动。即时生效并持久化。</summary>
    public bool AudioWidgetHueDrift
    {
        get => _audioHueDrift;
        set
        {
            if (!Set(ref _audioHueDrift, value)) return;
            _s.AudioWidgetHueDrift = value;
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private bool _audioClickThrough;
    /// <summary>鼠标穿透。即时生效并持久化。</summary>
    public bool AudioWidgetClickThrough
    {
        get => _audioClickThrough;
        set
        {
            if (!Set(ref _audioClickThrough, value)) return;
            _s.AudioWidgetClickThrough = value;
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private int _audioColorIndex;
    /// <summary>配色方案下拉框（0=主题强调色 1=彩虹 2=火焰 3=海洋 4=专辑封面色）。即时生效并持久化。</summary>
    public int AudioColorIndex
    {
        get => _audioColorIndex;
        set
        {
            var v = Math.Clamp(value, 0, AudioWaveOptions.ColorCount - 1);
            if (!Set(ref _audioColorIndex, v)) return;
            _s.AudioWidgetColor = AudioWaveOptions.ToToken((WaveColor)v);
            SettingsStore.Save(_s);
            AudioWidgetVisualChanged?.Invoke();
        }
    }

    private double _audioSensitivity;
    /// <summary>波形灵敏度（0.3–3.0）。即时生效并持久化。</summary>
    public double AudioWidgetSensitivity
    {
        get => _audioSensitivity;
        set
        {
            var v = Math.Clamp(Math.Round(value, 2), 0.3, 3.0);
            if (!Set(ref _audioSensitivity, v)) return;
            _s.AudioWidgetSensitivity = v;
            SettingsStore.Save(_s);
            OnPropertyChanged(nameof(AudioWidgetSensitivityText));
            AudioWidgetVisualChanged?.Invoke();
        }
    }
    public string AudioWidgetSensitivityText => $"{_audioSensitivity:0.0}×";

    /// <summary>调整风格/配色/灵敏度时触发，让桌面组件实时应用新的可视化设置。</summary>
    public event Action? AudioWidgetVisualChanged;

    // ── 硬件温度监控（即时生效并持久化）────────────────────────────────────
    public RelayCommand TestTtsCommand { get; }

    private bool _tempMonitorEnabled;
    public bool TempMonitorEnabled
    {
        get => _tempMonitorEnabled;
        set { if (Set(ref _tempMonitorEnabled, value)) { ApplyTempMonitor(); AuditLog.Action($"硬件温度监控：{(value ? "开启" : "关闭")}"); } }
    }

    private bool _tempTts;
    public bool TempTts { get => _tempTts; set { if (Set(ref _tempTts, value)) ApplyTempMonitor(); } }

    private bool _tempCpu;
    public bool TempCpu { get => _tempCpu; set { if (Set(ref _tempCpu, value)) ApplyTempMonitor(); } }
    private bool _tempGpu;
    public bool TempGpu { get => _tempGpu; set { if (Set(ref _tempGpu, value)) ApplyTempMonitor(); } }
    private bool _tempDisk;
    public bool TempDisk { get => _tempDisk; set { if (Set(ref _tempDisk, value)) ApplyTempMonitor(); } }

    private int _cpuTempThreshold;
    public int CpuTempThreshold { get => _cpuTempThreshold; set { if (Set(ref _cpuTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }
    private int _gpuTempThreshold;
    public int GpuTempThreshold { get => _gpuTempThreshold; set { if (Set(ref _gpuTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }
    private int _diskTempThreshold;
    public int DiskTempThreshold { get => _diskTempThreshold; set { if (Set(ref _diskTempThreshold, ClampTemp(value))) ApplyTempMonitor(); } }

    /// <summary>Reasonable warn-threshold bounds; keeps a typo from disabling (0) or never firing (999) alerts.</summary>
    private static int ClampTemp(int v) => Math.Clamp(v, 40, 110);

    /// <summary>Central reminder-frequency choices (seconds) — repeat-alert interval while a device stays hot.
    /// The order here matches the ComboBox items in SettingsView.</summary>
    private static readonly int[] ReminderSecondsOptions = { 30, 60, 120, 300, 600 };

    private int _reminderSeconds;
    /// <summary>Selected reminder-frequency index, bound to the settings ComboBox (centralized, not per-device).</summary>
    public int ReminderIndex
    {
        get { var i = Array.IndexOf(ReminderSecondsOptions, _reminderSeconds); return i >= 0 ? i : 1; }
        set
        {
            var sec = ReminderSecondsOptions[Math.Clamp(value, 0, ReminderSecondsOptions.Length - 1)];
            if (_reminderSeconds == sec) return;
            _reminderSeconds = sec;
            OnPropertyChanged();
            ApplyTempMonitor();
        }
    }

    /// <summary>Persist all temperature-monitor settings and reconfigure the background watchdog live.</summary>
    private void ApplyTempMonitor()
    {
        _s.TempMonitorEnabled = _tempMonitorEnabled;
        _s.TempTtsEnabled = _tempTts;
        _s.TempCpuEnabled = _tempCpu;
        _s.TempGpuEnabled = _tempGpu;
        _s.TempDiskEnabled = _tempDisk;
        _s.CpuTempThreshold = _cpuTempThreshold;
        _s.GpuTempThreshold = _gpuTempThreshold;
        _s.DiskTempThreshold = _diskTempThreshold;
        _s.TempReminderSeconds = _reminderSeconds;
        SettingsStore.Save(_s);
        TempMonitor.Configure(TempMonitorConfig.From(_s));
    }

    // ── 关闭主窗口行为（即时持久化）────────────────────────────────────
    private string _closeAction;
    public bool IsCloseAsk { get => _closeAction == "ask"; set { if (value) SetCloseAction("ask"); } }
    public bool IsCloseTray { get => _closeAction == "tray"; set { if (value) SetCloseAction("tray"); } }
    public bool IsCloseExit { get => _closeAction == "exit"; set { if (value) SetCloseAction("exit"); } }

    private void SetCloseAction(string a)
    {
        _closeAction = a;
        OnPropertyChanged(nameof(IsCloseAsk));
        OnPropertyChanged(nameof(IsCloseTray));
        OnPropertyChanged(nameof(IsCloseExit));
        _s.CloseAction = a;
        SettingsStore.Save(_s);
        AuditLog.Action($"关闭行为：{(a switch { "tray" => "最小化到后台常驻", "exit" => "直接退出", _ => "每次询问" })}");
    }

    // ── 终端特效（即时持久化并广播给终端页）──────────────────────────────
    public bool TermHacker
    {
        get => TerminalFx.Hacker;
        set { if (value != TerminalFx.Hacker) { TerminalFx.SetHacker(value); OnPropertyChanged(); } }
    }
    public bool TermCrt
    {
        get => TerminalFx.Crt;
        set { if (value != TerminalFx.Crt) { TerminalFx.SetCrt(value); OnPropertyChanged(); } }
    }
    public bool TermCodeRain
    {
        get => TerminalFx.CodeRain;
        set { if (value != TerminalFx.CodeRain) { TerminalFx.SetCodeRain(value); OnPropertyChanged(); OnPropertyChanged(nameof(CodeRainEnabled)); } }
    }
    public bool CodeRainEnabled => TerminalFx.CodeRain;

    public double TermCodeOpacity
    {
        get => TerminalFx.CodeOpacity;
        set { if (Math.Abs(value - TerminalFx.CodeOpacity) > 0.001) { TerminalFx.SetCodeOpacity(value); OnPropertyChanged(); OnPropertyChanged(nameof(TermCodeOpacityText)); } }
    }
    public string TermCodeOpacityText => $"{TerminalFx.CodeOpacity * 100:0}%";

    public double TermCodeSpeed
    {
        get => TerminalFx.Speed;
        set { if (Math.Abs(value - TerminalFx.Speed) > 0.001) { TerminalFx.SetSpeed(value); OnPropertyChanged(); OnPropertyChanged(nameof(TermCodeSpeedText)); } }
    }
    public string TermCodeSpeedText => $"{TerminalFx.Speed:0.0}×";

    // ── 界面语言（即时切换并持久化）────────────────────────────────────
    private string _lang;
    public bool IsLangZh { get => _lang == Lang.Zh; set { if (value) SetLang(Lang.Zh); } }
    public bool IsLangEn { get => _lang == Lang.En; set { if (value) SetLang(Lang.En); } }
    public bool IsLangDe { get => _lang == Lang.De; set { if (value) SetLang(Lang.De); } }

    private void SetLang(string code)
    {
        if (_lang == code) return;
        _lang = code;
        OnPropertyChanged(nameof(IsLangZh));
        OnPropertyChanged(nameof(IsLangEn));
        OnPropertyChanged(nameof(IsLangDe));
        LocalizationManager.SetLanguage(code);   // live: swaps S.* resources + raises CultureChanged
        SettingsStore.SetLanguage(code);
        AuditLog.Action($"切换语言：{code}");
    }

    private string _theme;
    public bool IsThemeSystem { get => _theme == "system"; set { if (value) SetTheme("system"); } }
    public bool IsThemeLight { get => _theme == "light"; set { if (value) SetTheme("light"); } }
    public bool IsThemeDark { get => _theme == "dark"; set { if (value) SetTheme("dark"); } }

    private void SetTheme(string t)
    {
        _theme = t;
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        ThemeManager.Apply(ThemeManager.Parse(t));
        _s.Theme = t;
        SettingsStore.Save(_s);
        AuditLog.Action($"切换主题：{t}");
    }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public event Action? Saved;

    private void Save()
    {
        _s.DevRoot = DevRoot.Trim();
        _s.ToolsDir = ToolsDir.Trim();
        _s.DownloadDir = DownloadDir.Trim();
        _s.RepoUrl = RepoUrl.Trim();
        _s.Mirror = Mirror.Trim();
        _s.RedactKeywords = RedactKeywords.Trim();
        SettingsStore.Save(_s);
        AuditLog.Action($"更新设置 · DevRoot={_s.DevRoot} · ToolsDir={_s.ToolsDir} · 下载={_s.DownloadDir} · Repo={_s.RepoUrl} · " +
                        $"镜像={(string.IsNullOrEmpty(_s.Mirror) ? "(无)" : _s.Mirror)} · 脱敏关键词 {ParseKeywords(_s.RedactKeywords).Length} 项");
        Note = Localizer.T("settings.saved");
        Saved?.Invoke();
    }

    public static string[] ParseKeywords(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ' ', '\n', '\r', '\t', ';', '，', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── 重置所有设置与应用数据（三重确认）──────────────────────────────────
    /// <summary>Wipe every setting and all app data back to a clean first-run state — behind three confirmations:
    /// two warning prompts, then a type-the-phrase gate. On success the data folder is deleted and the app restarts.</summary>
    private void ResetAll()
    {
        if (Dialogs.Show(Localizer.T("settings.reset.warn1.body"), Localizer.T("settings.reset.warn1.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        if (Dialogs.Show(Localizer.T("settings.reset.warn2.body"), Localizer.T("settings.reset.warn2.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var dlg = new ConfirmPhraseDialog(
            Localizer.T("settings.reset.confirm.title"),
            Localizer.T("settings.reset.confirm.body"),
            Localizer.T("settings.reset.confirm.phrase")) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        PerformResetAndRestart();
    }

    /// <summary>Clear app-controlled state (autostart), then delete the entire data folder and relaunch via a
    /// detached shell that waits for this process to exit first (so even locked files like the log are removed).</summary>
    private static void PerformResetAndRestart()
    {
        AuditLog.Action("重置：清除全部设置与应用数据并重启");
        try { Services.Sys.AutoStart.Set(false); } catch { /* best effort */ }
        try
        {
            var exe = Environment.ProcessPath;
            var folder = SettingsStore.Folder;
            // ping = ~2s grace for our process to fully exit; then remove the data folder and relaunch.
            var args = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{folder}\""
                       + (string.IsNullOrEmpty(exe) ? "" : $" & start \"\" \"{exe}\"");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", args)
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { /* if the shell can't be spawned, still shut down so a manual cleanup is possible */ }
        Application.Current.Shutdown();
    }
}
