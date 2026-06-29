using System.Windows;
using WinDeploy.App.Services.Clip;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Clip;

/// <summary>设置 tab: device name + ports, the auto-mirror-to-local toggle, the persist-history toggle, and
/// the board / image limits. Loads from the persisted config (so it shows the saved values even before the
/// share is started) and applies edits live via the manager on 保存.</summary>
public sealed class ClipSettingsViewModel : LocalizedObject
{
    private readonly ClipSyncManager _manager;

    public ClipSettingsViewModel(ClipSyncManager manager)
    {
        _manager = manager;
        SaveCommand = new RelayCommand(_ => Save());
        Load(ClipConfigStore.Load());
    }

    private string _deviceName = "";
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }

    private string _portText = "";
    public string PortText { get => _portText; set => Set(ref _portText, value); }

    private string _discoveryPortText = "";
    public string DiscoveryPortText { get => _discoveryPortText; set => Set(ref _discoveryPortText, value); }

    private bool _autoApply;
    public bool AutoApply { get => _autoApply; set => Set(ref _autoApply, value); }

    private bool _persistHistory;
    public bool PersistHistory { get => _persistHistory; set => Set(ref _persistHistory, value); }

    private string _historyLimitText = "";
    public string HistoryLimitText { get => _historyLimitText; set => Set(ref _historyLimitText, value); }

    private string _maxImageMbText = "";
    public string MaxImageMbText { get => _maxImageMbText; set => Set(ref _maxImageMbText, value); }

    public string CapInfo => Localizer.Format("clip.settings.capInfo", _manager.MaxPeers);

    public RelayCommand SaveCommand { get; }

    private void Load(ClipSyncConfig c)
    {
        DeviceName = c.DeviceName;
        PortText = c.Port.ToString();
        DiscoveryPortText = c.DiscoveryPort.ToString();
        AutoApply = c.AutoApplyToLocal;
        PersistHistory = c.PersistHistory;
        HistoryLimitText = c.HistoryLimit.ToString();
        MaxImageMbText = Math.Max(1, c.MaxImageBytes / (1024 * 1024)).ToString();
    }

    private void Save()
    {
        var name = DeviceName.Trim();
        if (name.Length == 0) { Warn(Localizer.T("clip.settings.nameRequired")); return; }
        if (!TryPort(PortText, out var port)) { Warn(Localizer.T("clip.settings.invalidPort")); return; }
        if (!TryPort(DiscoveryPortText, out var dport)) { Warn(Localizer.T("clip.settings.invalidPort")); return; }
        if (port == dport) { Warn(Localizer.T("clip.settings.portsClash")); return; }
        int.TryParse(HistoryLimitText.Trim(), out var limit);
        int.TryParse(MaxImageMbText.Trim(), out var mb);

        var cfg = new ClipSyncConfig
        {
            InstanceId = _manager.InstanceId,
            DeviceName = name,
            Port = port,
            DiscoveryPort = dport,
            AutoApplyToLocal = AutoApply,
            PersistHistory = PersistHistory,
            HistoryLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 5000),
            MaxImageBytes = Math.Clamp(mb <= 0 ? 4 : mb, 1, 16) * 1024 * 1024,   // keep under ClipProtocol.MaxFrame after base64
        };
        var restartNeeded = _manager.UpdateConfig(cfg);
        Load(cfg);   // reflect clamped values back into the fields

        AuditLog.Action($"剪贴板共享设置已保存 · 设备「{name}」· 端口 {port}");
        var msg = restartNeeded ? Localizer.T("clip.settings.savedRestart") : Localizer.T("clip.settings.saved");
        Dialogs.Show(msg, Localizer.T("clip.tab.settings"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool TryPort(string s, out int port)
        => int.TryParse(s.Trim(), out port) && port is >= 1 and <= 65535;

    private static void Warn(string m) => Dialogs.Show(m, Localizer.T("clip.tab.settings"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
