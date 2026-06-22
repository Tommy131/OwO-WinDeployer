using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

public sealed class WslDistroRowViewModel
{
    public WslDistro D { get; }
    public WslDistroRowViewModel(WslDistro d) { D = d; }
    public string Name => D.Name;
    public string State => D.State;
    public string Version => $"WSL{D.Version}";
    public bool IsDefault => D.Default;
    public bool Running => string.Equals(D.State, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The "WSL" page (开发人员模式): list installed distros, install from the online catalog, set
/// default, launch, terminate, export (backup .tar) and unregister. Dev-only.</summary>
public sealed class WslViewModel : ObservableObject
{
    public ObservableCollection<WslDistroRowViewModel> Distros { get; } = new();
    public ObservableCollection<WslOnline> Online { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand ShutdownCommand { get; }
    public RelayCommand SetDefaultCommand { get; }
    public RelayCommand TerminateCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand UnregisterCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenFeaturesCommand { get; }
    public RelayCommand EnableFeatureCommand { get; }

    public WslViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        InstallCommand = new RelayCommand(_ => Install(), _ => FeatureEnabled && SelectedOnline != null);
        ShutdownCommand = new RelayCommand(_ => _ = ActAsync(Wsl.ShutdownAsync(), "已关闭所有 WSL 实例"));
        SetDefaultCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ActAsync(Wsl.SetDefaultAsync(r.Name), $"已设为默认：{r.Name}"); });
        TerminateCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ActAsync(Wsl.TerminateAsync(r.Name), $"已停止：{r.Name}"); });
        ExportCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = ExportAsync(r); });
        UnregisterCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) _ = UnregisterAsync(r); });
        LaunchCommand = new RelayCommand(p => { if (p is WslDistroRowViewModel r) Wsl.LaunchVisible($"-d \"{r.Name}\""); });
        OpenFeaturesCommand = new RelayCommand(_ => { var (_, m) = Wsl.OpenWindowsFeatures(); Note = m + "：勾选「适用于 Linux 的 Windows 子系统」与「虚拟机平台」，确定后重启，再回到本页点刷新。"; });
        EnableFeatureCommand = new RelayCommand(_ => { var (ok, m) = Wsl.EnableFeatureVisible(); Note = ok ? "正在启用 WSL 功能（请在新窗口允许 UAC，完成后重启系统再刷新）。" : m; });
        _ = LoadAsync();
    }

    /// <summary>True only when the WSL optional feature is actually enabled (not just wsl.exe present).</summary>
    private bool _featureEnabled = true;
    public bool FeatureEnabled
    {
        get => _featureEnabled;
        set { if (Set(ref _featureEnabled, value)) { OnPropertyChanged(nameof(FeatureDisabled)); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool FeatureDisabled => !_featureEnabled;

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    private WslOnline? _selectedOnline;
    public WslOnline? SelectedOnline { get => _selectedOnline; set { if (Set(ref _selectedOnline, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private async Task LoadAsync()
    {
        FeatureEnabled = Wsl.IsFeatureEnabled();
        if (!FeatureEnabled)
        {
            Distros.Clear();
            Online.Clear();
            Note = "WSL 功能未启用：请先在「控制面板 → 程序 → 启用或关闭 Windows 功能」勾选 WSL 后再下载发行版。";
            return;
        }

        Note = "正在读取 WSL 发行版 …";
        var distros = await Wsl.ListAsync();
        Distros.Clear();
        foreach (var d in distros) Distros.Add(new WslDistroRowViewModel(d));

        if (Online.Count == 0)
        {
            try { foreach (var o in await Wsl.ListOnlineAsync()) Online.Add(o); } catch { /* offline */ }
        }
        var wsl2 = Wsl.IsVmPlatformEnabled() ? "" : "（注意：未启用「虚拟机平台」，WSL2 不可用，可在 Windows 功能中补勾选）";
        Note = $"已安装 {Distros.Count} 个发行版{wsl2}";
    }

    private void Install()
    {
        var name = SelectedOnline?.Name;
        if (name == null) return;
        var (ok, msg) = Wsl.InstallVisible(name);
        Note = ok ? $"已在新窗口中安装 {name}（完成后点刷新）" : msg;
    }

    private async Task ActAsync(Task<(bool Ok, string Msg)> op, string okMsg)
    {
        var (ok, msg) = await op;
        Note = ok ? okMsg : msg;
        await LoadAsync();
    }

    private async Task ExportAsync(WslDistroRowViewModel r)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"导出 {r.Name}", FileName = $"{r.Name}.tar", Filter = "tar 备份 (*.tar)|*.tar",
        };
        if (dlg.ShowDialog() != true) return;
        Note = $"正在导出 {r.Name} …（较大发行版可能耗时数分钟）";
        var (ok, msg) = await Wsl.ExportAsync(r.Name, dlg.FileName);
        Note = msg;
        if (ok) AuditLog.Action($"WSL 导出：{r.Name} → {dlg.FileName}");
    }

    private async Task UnregisterAsync(WslDistroRowViewModel r)
    {
        if (MessageBox.Show($"注销并删除发行版「{r.Name}」？\n\n这会删除该发行版的全部数据，不可恢复！\n建议先「导出备份」。",
                "注销 WSL 发行版", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = await Wsl.UnregisterAsync(r.Name);
        Note = ok ? $"已注销：{r.Name}" : msg;
        if (ok) AuditLog.Action($"WSL 注销：{r.Name}");
        await LoadAsync();
    }
}
