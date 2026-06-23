using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>The "服务配置" page host. Implements nested navigation: it shows a <see cref="ServerListViewModel"/>
/// (home — all supported servers) and, when one is opened, swaps to a <see cref="ServerDetailViewModel"/>
/// (per-server management). The active sub-page is exposed via <see cref="Current"/>.</summary>
public sealed class ServiceConfigViewModel : ObservableObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private ServerListViewModel? _list;

    private object? _current;
    public object? Current { get => _current; private set => Set(ref _current, value); }

    public void Initialize(Catalog catalog, PathResolver resolver)
    {
        _catalog = catalog;
        _resolver = resolver;
        _list = new ServerListViewModel(catalog, resolver, OpenServer);
        Current = _list;
    }

    private void OpenServer(ServerInfo info)
    {
        var detail = new ServerDetailViewModel(info, () => { _list?.Refresh(); Current = _list; });
        Current = detail;
    }
}

// ── home: list of supported servers ──────────────────────────────────────────
public sealed class ServerCardViewModel : ObservableObject
{
    public ServerInfo Info { get; }
    public ServerCardViewModel(ServerInfo info) { Info = info; }
    public string Name => Info.Name;
    public string Dir => Info.Dir;
    public string KindTag => Info.Id;
    public int ConfigCount => Info.Configs.Count;
    public string Summary
    {
        get
        {
            var bits = new List<string> { $"{ConfigCount} 个配置文件" };
            if (Info.SupportsVhost) bits.Add("站点 vhost");
            if (Info.SupportsSsl) bits.Add("SSL");
            if (Info.HasService) bits.Add("进程管理");
            return string.Join(" · ", bits);
        }
    }

    private bool _running;
    public bool Running { get => _running; set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); } } }
    public string StatusText => !Info.HasService ? "—" : _running ? "运行中" : "已停止";
    public string StatusBrush => _running ? "OkFg" : "TextTertiary";
}

public sealed class ServerListViewModel : ObservableObject
{
    private readonly Catalog _catalog;
    private readonly PathResolver _resolver;
    private readonly Action<ServerInfo> _open;

    public ObservableCollection<ServerCardViewModel> Servers { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }

    public ServerListViewModel(Catalog catalog, PathResolver resolver, Action<ServerInfo> open)
    {
        _catalog = catalog;
        _resolver = resolver;
        _open = open;
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenCommand = new RelayCommand(p => { if (p is ServerCardViewModel c) _open(c.Info); });
        Refresh();
    }

    public bool HasServers => Servers.Count > 0;
    public bool NoServers => Servers.Count == 0;

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public void Refresh()
    {
        Servers.Clear();
        foreach (var s in ServiceConfig.Detect(_catalog, _resolver))
            Servers.Add(new ServerCardViewModel(s));
        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(NoServers));
        Note = Servers.Count == 0
            ? "未检测到受支持的 Web 服务端，请前往软件安装中心安装 nginx / Apache / Tomcat / PHP 后在此处管理。"
            : $"已检测到 {Servers.Count} 个服务端，点击进入可管理配置、SSL、站点与进程。";
        _ = ProbeStatusAsync();
    }

    private async Task ProbeStatusAsync()
    {
        foreach (var c in Servers.ToList())
        {
            if (!c.Info.HasService) continue;
            try { var rt = await ServerManager.GetRuntimeAsync(c.Info); c.Running = rt.Running; }
            catch { /* ignore */ }
        }
    }
}

// ── detail: per-server management ────────────────────────────────────────────
public sealed class ServerDetailViewModel : ObservableObject
{
    public ServerInfo Info { get; }
    private readonly Action _back;
    private DispatcherTimer? _timer;
    private bool _busy;

    public ServerDetailViewModel(ServerInfo info, Action back)
    {
        Info = info;
        _back = back;

        BackCommand = new RelayCommand(_ => { StopLive(); _back(); });
        OpenDirCommand = new RelayCommand(_ => OpenPath(Info.Dir));

        OpenConfigCommand = new RelayCommand(p => { if (p is ConfigFile f) LoadFile(f.Path); });
        SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);

        StartCommand = new RelayCommand(_ => Act(SvcAction.Start));
        StopCommand = new RelayCommand(_ => Act(SvcAction.Stop));
        ReloadCommand = new RelayCommand(_ => Act(SvcAction.Reload));
        RestartCommand = new RelayCommand(_ => Act(SvcAction.Restart));

        ShowLogCommand = new RelayCommand(p => { if (p is LogFile l) new Views.LogViewerDialog(Info.Name, l.Path) { Owner = App() }.ShowDialog(); RefreshLogs(); });
        ClearLogCommand = new RelayCommand(p => { if (p is LogFile l) { var (_, m) = ServerManager.ClearLog(l.Path); Note = $"{l.Name}：{m}"; RefreshLogs(); } });
        CollectLogsCommand = new RelayCommand(_ => CollectLogs());
        OpenLogDirCommand = new RelayCommand(_ => OpenPath(Info.LogDir));
        RefreshLogsCommand = new RelayCommand(_ => RefreshLogs());

        CreateCertCommand = new RelayCommand(_ => CreateCert());
        ImportCertCommand = new RelayCommand(_ => ImportCert());
        DeleteCertCommand = new RelayCommand(p => { if (p is CertFile c) DeleteCert(c); });
        OpenSslDirCommand = new RelayCommand(_ => OpenPath(Info.SslDir));
        RefreshCertsCommand = new RelayCommand(_ => RefreshCerts());

        CreateVhostCommand = new RelayCommand(_ => CreateVhost());
        EditVhostCommand = new RelayCommand(p => { if (p is ConfigFile f) LoadFile(f.Path); });
        DeleteVhostCommand = new RelayCommand(p => { if (p is ConfigFile f) DeleteVhost(f); });
        OpenVhostDirCommand = new RelayCommand(_ => OpenPath(Info.VhostDir));
        RefreshVhostsCommand = new RelayCommand(_ => RefreshVhosts());

        foreach (var c in Info.Configs) Configs.Add(c);
        RefreshLogs();
        if (Info.SupportsSsl) RefreshCerts();
        if (Info.SupportsVhost) RefreshVhosts();
    }

    // identity
    public string Name => Info.Name;
    public string Dir => Info.Dir;
    public string KindTag => Info.Id;
    public bool SupportsSsl => Info.SupportsSsl;
    public bool SupportsVhost => Info.SupportsVhost;
    public bool HasService => Info.HasService;

    public ObservableCollection<ConfigFile> Configs { get; } = new();
    public ObservableCollection<LogFile> Logs { get; } = new();
    public ObservableCollection<CertFile> Certs { get; } = new();
    public ObservableCollection<ConfigFile> Vhosts { get; } = new();

    public RelayCommand BackCommand { get; }
    public RelayCommand OpenDirCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CollectLogsCommand { get; }
    public RelayCommand OpenLogDirCommand { get; }
    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand CreateCertCommand { get; }
    public RelayCommand ImportCertCommand { get; }
    public RelayCommand DeleteCertCommand { get; }
    public RelayCommand OpenSslDirCommand { get; }
    public RelayCommand RefreshCertsCommand { get; }
    public RelayCommand CreateVhostCommand { get; }
    public RelayCommand EditVhostCommand { get; }
    public RelayCommand DeleteVhostCommand { get; }
    public RelayCommand OpenVhostDirCommand { get; }
    public RelayCommand RefreshVhostsCommand { get; }

    /// <summary>Raised when a file is loaded into the editor: (content, path).</summary>
    public event Action<string, string>? FileOpened;

    private string _note = "管理该服务端的配置、SSL 证书、站点与进程。";
    public string Note { get => _note; set => Set(ref _note, value); }

    public bool HasLogs => Logs.Count > 0;

    // ── runtime status ───────────────────────────────────────────────────────
    private bool _running;
    public bool Running
    {
        get => _running;
        set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); RaiseActionVisibility(); } }
    }
    public string StatusText => !HasService ? "无进程" : _running ? "运行中" : "已停止";
    public string StatusBrush => !HasService ? "TextTertiary" : _running ? "OkFg" : "FailFg";

    private string _pidText = "—";
    public string PidText { get => _pidText; set => Set(ref _pidText, value); }

    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; set => Set(ref _uptimeText, value); }

    public bool ShowStart => Info.CanStart && !_running;
    public bool ShowStop => Info.CanStop && _running;
    public bool ShowReload => Info.CanReload && _running;
    public bool ShowRestart => Info.CanRestart && _running;

    private void RaiseActionVisibility()
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(ShowReload));
        OnPropertyChanged(nameof(ShowRestart));
    }

    public void StartLive()
    {
        if (!HasService) return;
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _ = RefreshRuntimeAsync();
        _timer.Start();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e) => _ = RefreshRuntimeAsync();

    private async Task RefreshRuntimeAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var rt = await ServerManager.GetRuntimeAsync(Info);
            Running = rt.Running;
            PidText = rt.PidText;
            UptimeText = rt.Started is DateTime t ? FormatUptime(DateTime.Now - t) : "—";
        }
        catch { /* ignore transient */ }
        finally { _busy = false; }
    }

    private static string FormatUptime(TimeSpan up)
    {
        if (up.TotalSeconds < 0) up = TimeSpan.Zero;
        if (up.TotalDays >= 1) return $"{(int)up.TotalDays} 天 {up.Hours} 小时";
        if (up.TotalHours >= 1) return $"{(int)up.TotalHours} 小时 {up.Minutes} 分";
        if (up.TotalMinutes >= 1) return $"{(int)up.TotalMinutes} 分 {up.Seconds} 秒";
        return $"{(int)up.TotalSeconds} 秒";
    }

    // ── config editor ──────────────────────────────────────────────────────────
    private string? _currentPath;
    public string CurrentPath => _currentPath ?? "未选择文件";
    public bool HasOpenFile => _currentPath != null;

    private string _editor = "";
    public string Editor { get => _editor; set { if (Set(ref _editor, value)) OnPropertyChanged(nameof(CanSave)); } }
    public bool CanSave => _currentPath != null;

    private void LoadFile(string path)
    {
        try
        {
            Editor = File.ReadAllText(path);
            _currentPath = path;
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(HasOpenFile));
            OnPropertyChanged(nameof(CanSave));
            FileOpened?.Invoke(Editor, path);
        }
        catch (Exception ex) { Note = "读取失败：" + ex.Message; }
    }

    private void Save()
    {
        if (_currentPath == null) return;
        try
        {
            if (File.Exists(_currentPath))
                File.Copy(_currentPath, $"{_currentPath}.bak.{DateTime.Now:yyyyMMddHHmmss}", true);
            File.WriteAllText(_currentPath, Editor);
            AuditLog.Action($"服务配置：保存 {_currentPath}");
            Note = $"已保存（已备份 .bak）：{Path.GetFileName(_currentPath)}";
        }
        catch (Exception ex) { Note = "保存失败：" + ex.Message; }
    }

    // ── service actions ──────────────────────────────────────────────────────
    private void Act(SvcAction action)
    {
        var (ok, msg) = ServiceConfig.Run(Info, action);
        var verb = action switch { SvcAction.Start => "启动", SvcAction.Stop => "停止", SvcAction.Reload => "重载", _ => "重启" };
        AuditLog.Action($"服务配置：{verb} {Info.Name} — {(ok ? "成功" : "失败")} {msg}".TrimEnd());
        Note = $"{verb}：{msg}";
        if (!ok) MessageBox.Show(msg, $"{verb} {Info.Name}", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = DelayedRuntimeRefresh();
    }

    private async Task DelayedRuntimeRefresh()
    {
        await Task.Delay(900);
        await RefreshRuntimeAsync();
    }

    // ── logs ───────────────────────────────────────────────────────────────────
    private void RefreshLogs()
    {
        Logs.Clear();
        foreach (var l in ServerManager.Logs(Info)) Logs.Add(l);
        OnPropertyChanged(nameof(HasLogs));
    }

    private void CollectLogs()
    {
        var logs = ServerManager.Logs(Info);
        if (logs.Count == 0) { Note = "没有可采集的日志文件。"; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "采集日志（导出尾部）",
            FileName = $"{Info.Id}-logs-{Environment.MachineName}.txt",
            Filter = "文本文件 (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var w = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(false));
            foreach (var l in logs)
            {
                w.WriteLine($"================ {l.Name} ({l.SizeText}) ================");
                w.WriteLine(ServerManager.ReadTail(l.Path, 1000));
                w.WriteLine();
            }
            AuditLog.Action($"服务配置：采集 {Info.Name} 日志 {logs.Count} 个 → {dlg.FileName}");
            Note = $"已采集 {logs.Count} 个日志 → {dlg.FileName}";
            OpenPath(dlg.FileName);
        }
        catch (Exception ex) { Note = "采集失败：" + ex.Message; }
    }

    // ── SSL ──────────────────────────────────────────────────────────────────
    private void RefreshCerts()
    {
        Certs.Clear();
        foreach (var c in ServerManager.ListCerts(Info)) Certs.Add(c);
    }

    private void CreateCert()
    {
        var dlg = new Views.InputDialog("生成自签名证书", "为该域名 / 主机生成自签名证书与私钥（PEM，有效期 5 年）：", "example.local") { Owner = App() };
        if (dlg.ShowDialog() != true) return;
        var (ok, msg) = ServerManager.CreateSelfSigned(Info, dlg.Value);
        Note = msg;
        if (!ok) MessageBox.Show(msg, "生成证书", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshCerts();
    }

    private void ImportCert()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入证书 / 私钥",
            Filter = "证书与私钥 (*.crt;*.cer;*.pem;*.key;*.pfx)|*.crt;*.cer;*.pem;*.key;*.pfx|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        var n = 0;
        foreach (var f in dlg.FileNames) { var (ok, _) = ServerManager.ImportCert(Info, f); if (ok) n++; }
        Note = $"已导入 {n} 个文件到 SSL 目录。";
        RefreshCerts();
    }

    private void DeleteCert(CertFile c)
    {
        if (MessageBox.Show($"确定删除证书文件？\n{c.Name}", "删除证书", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = ServerManager.DeleteCert(c.Path);
        Note = $"{c.Name}：{msg}";
        if (ok) RefreshCerts();
    }

    // ── vhosts ─────────────────────────────────────────────────────────────────
    private void RefreshVhosts()
    {
        Vhosts.Clear();
        foreach (var v in ServerManager.ListVhosts(Info)) Vhosts.Add(v);
    }

    private void CreateVhost()
    {
        var dlg = new Views.VhostDialog(Info) { Owner = App() };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var (ok, msg) = ServerManager.CreateVhost(Info, dlg.Result);
        Note = msg;
        if (!ok) MessageBox.Show(msg, "新建站点", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshVhosts();
    }

    private void DeleteVhost(ConfigFile f)
    {
        if (MessageBox.Show($"确定删除站点配置？\n{f.Name}\n\n（删除后请重载 / 重启服务生效）", "删除站点", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = ServerManager.DeleteVhost(f.Path);
        Note = $"{f.Name}：{msg}";
        if (ok) RefreshVhosts();
    }

    // ── helpers ───────────────────────────────────────────────────────────────
    private static Window? App() => Application.Current.MainWindow;

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else Note = "路径不存在：" + path;
        }
        catch (Exception ex) { Note = "打开失败：" + ex.Message; }
    }
}
