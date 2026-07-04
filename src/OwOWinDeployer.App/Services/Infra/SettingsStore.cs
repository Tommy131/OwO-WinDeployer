using System.IO;
using System.Text.Json;

namespace OwOWinDeployer.App.Services.Infra;

public sealed class AppSettings
{
    public string? DevRoot { get; set; }
    public string? ToolsDir { get; set; }
    public string? DownloadDir { get; set; }
    public string? RepoUrl { get; set; }
    public string? Mirror { get; set; }
    public string? RedactKeywords { get; set; }

    /// <summary>下载应用 / 安装包时通过代理网络。仅在连通性测试通过后才会保存。默认关闭。</summary>
    public bool ProxyEnabled { get; set; }
    /// <summary>代理地址：http(s):// 或 socks5/socks4://[user:pass@]host:port。</summary>
    public string? ProxyUrl { get; set; }
    public string? Theme { get; set; }   // system | light | dark

    /// <summary>接收预览版（pre-release）更新提示。默认关闭；若当前运行的就是预览版，则无论此项如何都会检测预览版。</summary>
    public bool PreReleaseUpdates { get; set; }
    /// <summary>用户选择「忽略此版本」的发布 tag（如 v1.3.1）。启动检查遇到同一 tag 时不再打扰；出现更新的版本会重新提示。手动「检查更新」不受影响。</summary>
    public string? IgnoredUpdateVersion { get; set; }

    /// <summary>上次已展示「欢迎/使用须知」弹窗时的应用版本。与当前版本不同（含首次运行为 null）时启动后弹一次，
    /// 再次强调免费社区版使用须知与联系渠道。</summary>
    public string? LastWelcomedVersion { get; set; }

    /// <summary>界面语言：zh | en | de。null 表示首次运行未设定（按系统语言自动选择）。</summary>
    public string? Language { get; set; }

    /// <summary>关闭主窗口时的行为：ask（每次询问，默认）| tray（最小化到后台常驻）| exit（直接退出）。</summary>
    public string? CloseAction { get; set; }

    /// <summary>始终在系统托盘显示常驻图标：开启后无论窗口是否最小化都常驻一个托盘图标。默认关闭。</summary>
    public bool AlwaysShowTray { get; set; }

    /// <summary>启动应用时自动打开「快速启动」页（否则落在默认首页）。默认开启。</summary>
    public bool ShowLauncherOnStartup { get; set; } = true;

    /// <summary>在桌面显示「快速启动」毛玻璃小组件（类 Win7 桌面 gadget）。默认关闭（opt-in）。</summary>
    public bool ShowDesktopWidget { get; set; }
    /// <summary>小组件在屏幕上的位置（左/上，像素）。null 表示用默认位置（工作区右上角）。</summary>
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    /// <summary>小组件是否「永远置顶」（图钉）。默认关闭。</summary>
    public bool WidgetPinned { get; set; }
    /// <summary>小组件背景填充不透明度 0.1–0.85（越低越通透）。默认 0.4（透视程度 60%）。</summary>
    public double WidgetOpacity { get; set; } = 0.4;
    /// <summary>小组件使用原生 DWM 毛玻璃（即时、无圆角）而非 WPF 截图模糊（平滑圆角、拖动略延迟）。默认 false。</summary>
    public bool WidgetNativeGlass { get; set; }

    // ── 音频波形桌面组件（实时可视化当前设备的音频输出，WASAPI loopback）───────────────
    /// <summary>在桌面显示「音频波形」毛玻璃小组件。默认关闭（opt-in）。仅在显示时采集音频。</summary>
    public bool ShowAudioWidget { get; set; }
    /// <summary>音频组件在屏幕上的位置（左/上，像素）。null 表示用默认位置。</summary>
    public double? AudioWidgetLeft { get; set; }
    public double? AudioWidgetTop { get; set; }
    /// <summary>音频组件是否「永远置顶」（图钉）。默认关闭。</summary>
    public bool AudioWidgetPinned { get; set; }
    /// <summary>音频组件背景填充不透明度 0.1–0.85（越低越通透）。默认 0.35。</summary>
    public double AudioWidgetOpacity { get; set; } = 0.35;
    /// <summary>音频组件使用原生 DWM 毛玻璃（即时、无圆角）而非 WPF 截图模糊。默认 false。</summary>
    public bool AudioWidgetNativeGlass { get; set; }
    /// <summary>波形风格：mirror（镜像频谱，默认）| bars（频谱柱）| scope（示波器）| radial（环形频谱）。</summary>
    public string? AudioWidgetStyle { get; set; }
    /// <summary>配色方案：accent（主题强调色，默认）| rainbow（彩虹）| fire（火焰）| ocean（海洋）。</summary>
    public string? AudioWidgetColor { get; set; }
    /// <summary>灵敏度倍率 0.3–3.0（越大波形越高）。默认 1.0。</summary>
    public double AudioWidgetSensitivity { get; set; } = 1.0;
    /// <summary>波形随节拍脉冲（检测到鼓点时辉光炸开 + 微缩放）。默认开启。</summary>
    public bool AudioWidgetBeatReactive { get; set; } = true;
    /// <summary>静音时波形平滑淡出「呼吸」，来声音再唤醒。默认开启。</summary>
    public bool AudioWidgetSilenceFade { get; set; } = true;
    /// <summary>在组件角落显示实时读数（音名 · BPM · 电平）。默认开启。</summary>
    public bool AudioWidgetReadout { get; set; } = true;
    /// <summary>辉光强度倍率 0.2–2.5。默认 1.0。</summary>
    public double AudioWidgetGlow { get; set; } = 1.0;
    /// <summary>色相随时间缓慢流动（强调色 / 专辑 / 彩虹）。默认关闭。</summary>
    public bool AudioWidgetHueDrift { get; set; }
    /// <summary>鼠标穿透：开启后点击穿过组件落到桌面（组件变为纯装饰、不可交互，需在设置页关闭）。默认关闭。</summary>
    public bool AudioWidgetClickThrough { get; set; }

    // ── 硬件温度监控（后台定时检测 CPU / GPU / NVMe 硬盘，超阈值时通知 + 可选 TTS 语音）──────────
    /// <summary>启用硬件温度监控。默认关闭（opt-in）。</summary>
    public bool TempMonitorEnabled { get; set; }
    /// <summary>超温时用系统 TTS 语音播报（按界面语言）。默认开启。</summary>
    public bool TempTtsEnabled { get; set; } = true;
    public bool TempCpuEnabled { get; set; } = true;
    public bool TempGpuEnabled { get; set; } = true;
    public bool TempDiskEnabled { get; set; } = true;
    /// <summary>各硬件告警阈值（摄氏度）。</summary>
    public int CpuTempThreshold { get; set; } = 90;
    public int GpuTempThreshold { get; set; } = 85;
    public int DiskTempThreshold { get; set; } = 65;
    /// <summary>温度持续过高时的重复提醒间隔（秒）。默认 60s。用户可在超温弹窗中调整。</summary>
    public int TempReminderSeconds { get; set; } = 60;

    /// <summary>开发人员模式：开启后软件安装中心显示全部分类；关闭时普通用户仅见
    /// 办公/通讯、游戏平台、系统依赖、媒体四类。默认关闭。</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>批量安装前先创建系统还原点（Checkpoint-Computer，需管理员且已启用系统还原）。默认关闭。</summary>
    public bool RestorePointBeforeApply { get; set; }

    /// <summary>终端「黑客风格」配色（绿色磷光 + 辉光 + 近黑底）。null 视为默认开启。</summary>
    public bool? TerminalHackerFx { get; set; }

    /// <summary>终端 CRT 特效（扫描线 + 轻微闪烁）。null 视为默认开启。</summary>
    public bool? TerminalCrtFx { get; set; }

    /// <summary>终端背景代码滚动特效。null 视为默认开启。</summary>
    public bool? TerminalCodeRain { get; set; }

    /// <summary>背景代码不透明度 0.05–1.0（越低前景越清晰）。null 视为默认 0.4。</summary>
    public double? TerminalCodeOpacity { get; set; }

    /// <summary>背景代码滚动速度倍率 0.2–4.0。null 视为默认 1.0。</summary>
    public double? TerminalCodeSpeed { get; set; }

    /// <summary>Custom install locations chosen per item (id → path), so a portable/git/winget app
    /// installed outside its default location is still found after a restart.</summary>
    public Dictionary<string, string> InstallPaths { get; set; } = new();
}

/// <summary>Persists GUI settings to %LOCALAPPDATA%/OwOWinDeployer/settings.json.</summary>
public static class SettingsStore
{
    private static readonly string DirPath = AppPaths.DataRoot;
    private static readonly string FilePathValue = Path.Combine(DirPath, "settings.json");
    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static string FilePath => FilePathValue;
    public static string Folder => DirPath;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePathValue))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePathValue)) ?? new();
        }
        catch { /* fall through to defaults */ }

        // Create the file on first run so the path shown in the UI always exists.
        var def = new AppSettings();
        Save(def);
        return def;
    }

    public static void Save(AppSettings s)
    {
        try { Directory.CreateDirectory(DirPath); File.WriteAllText(FilePathValue, JsonSerializer.Serialize(s, Opt)); }
        catch { /* best effort */ }
    }

    /// <summary>Persist the close-button behavior (ask | tray | exit). Load-modify-save.</summary>
    public static void SetCloseAction(string action)
    {
        try { var s = Load(); s.CloseAction = action; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Persist the UI language (zh | en | de). Load-modify-save.</summary>
    public static void SetLanguage(string code)
    {
        try { var s = Load(); s.Language = code; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Persist the terminal hacker-FX toggle. Load-modify-save.</summary>
    public static void SetTerminalHackerFx(bool on)
    {
        try { var s = Load(); s.TerminalHackerFx = on; Save(s); } catch { /* best effort */ }
    }

    /// <summary>Remember (or forget, when <paramref name="path"/> is null/empty) a per-item custom install
    /// path. Load-modify-save so it never clobbers other settings.</summary>
    public static void SetInstallPath(string id, string? path)
    {
        try
        {
            var s = Load();
            s.InstallPaths ??= new();
            if (string.IsNullOrWhiteSpace(path)) s.InstallPaths.Remove(id);
            else s.InstallPaths[id] = path.Trim();
            Save(s);
        }
        catch { /* best effort */ }
    }
}
