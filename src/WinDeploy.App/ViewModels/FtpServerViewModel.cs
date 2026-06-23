using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using WinDeploy.App.Services;
using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.ViewModels;

/// <summary>A live view of one connected session for the monitor table.</summary>
public sealed class FtpConnRowVm : ObservableObject
{
    private readonly FtpConnectionInfo _info;
    public FtpConnRowVm(FtpConnectionInfo info) { _info = info; }
    public int Id => _info.Id;
    public string Remote => _info.Remote;
    public string User => _info.User;
    public string Activity => _info.Activity;
    public string SinceText
    {
        get
        {
            var d = DateTime.Now - _info.ConnectedAt;
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds}s" : $"{(int)d.TotalSeconds}s";
        }
    }
    public string TransferText => $"↑{Mb(_info.BytesUp)} ↓{Mb(_info.BytesDown)}";
    public void Refresh() { OnPropertyChanged(nameof(User)); OnPropertyChanged(nameof(Activity)); OnPropertyChanged(nameof(SinceText)); OnPropertyChanged(nameof(TransferText)); }
    private static string Mb(long b) => b >= 1024 * 1024 ? $"{b / 1024.0 / 1024:0.0}M" : b >= 1024 ? $"{b / 1024.0:0.0}K" : $"{b}B";
}

/// <summary>The 服务端 tab: starts/stops the FTP/FTPS listener using the saved config, and shows live status,
/// the connection table, reachable addresses, and a rolling protocol log.</summary>
public sealed class FtpServerViewModel : ObservableObject
{
    private readonly FtpServer _server;
    private readonly Func<FtpServerConfig> _configProvider;
    private readonly Queue<string> _logLines = new();
    private DispatcherTimer? _timer;

    public FtpServerViewModel(FtpServer server, Func<FtpServerConfig> configProvider)
    {
        _server = server;
        _configProvider = configProvider;
        StartCommand = new RelayCommand(_ => Start(), _ => !Running);
        StopCommand = new RelayCommand(_ => Stop(), _ => Running);
        ClearLogCommand = new RelayCommand(_ => { _logLines.Clear(); LogText = ""; });

        _server.Logged += OnLogged;
        _server.ConnectionsChanged += OnConnectionsChanged;
        LocalAddresses = string.Join("   ", LocalIPv4());
    }

    public ObservableCollection<FtpConnRowVm> Connections { get; } = new();

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public bool Running => _server.Running;
    public string StatusText => _server.Running ? "运行中" : "已停止";
    public string StatusBrush => _server.Running ? "OkFg" : "TextTertiary";
    public string LocalAddresses { get; }

    private string _endpointText = "未启动";
    public string EndpointText { get => _endpointText; private set => Set(ref _endpointText, value); }

    private string _uptimeText = "—";
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

    private string _connCountText = "0 个连接";
    public string ConnCountText { get => _connCountText; private set => Set(ref _connCountText, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    public bool NoConnections => Connections.Count == 0;

    private void Start()
    {
        FtpServerConfig cfg;
        try { cfg = _configProvider(); }
        catch (Exception ex) { Error("读取配置失败：" + ex.Message); return; }

        if (cfg.Users.Count == 0 && !cfg.AllowAnonymous)
        {
            if (MessageBox.Show("当前没有任何用户，且未启用匿名访问。\n客户端将无法登录。仍要启动吗？",
                    "启动 FTP 服务端", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }

        try
        {
            _server.Start(cfg);
            AuditLog.Action($"FTP 服务端启动 · 端口 {cfg.Port} · TLS {cfg.TlsMode}");
            RefreshState();
            StartLive();
        }
        catch (Exception ex)
        {
            AuditLog.Action("FTP 服务端启动失败：" + ex.Message);
            Error("启动失败：" + ex.Message +
                "\n\n常见原因：端口被占用、被防火墙拦截，或证书无效。可尝试更换端口。");
        }
    }

    private void Stop()
    {
        _server.Stop();
        AuditLog.Action("FTP 服务端停止");
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        CommandManager_Invalidate();
        if (_server.Running)
        {
            var cfg = _server.Config;
            var bits = new List<string> { $"控制端口 {cfg.Port}" };
            if (cfg.ImplicitTls) bits.Add($"隐式 TLS {cfg.ImplicitPort}");
            else if (cfg.TlsEnabled) bits.Add("显式 AUTH TLS");
            else bits.Add("明文");
            bits.Add($"被动 {cfg.PassiveMin}-{cfg.PassiveMax}");
            EndpointText = string.Join(" · ", bits);
        }
        else EndpointText = "未启动";
        RefreshConnections();
    }

    private static void CommandManager_Invalidate()
        => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private void OnLogged(FtpLogEntry e)
    {
        var line = $"{e.Time:HH:mm:ss} " + (e.ConnId > 0 ? $"[#{e.ConnId}] " : "") + e.Text;
        Dispatcher().BeginInvoke(() =>
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > 500) _logLines.Dequeue();
            LogText = string.Join("\n", _logLines);
        });
    }

    private void OnConnectionsChanged() => Dispatcher().BeginInvoke(RefreshConnections);

    private void RefreshConnections()
    {
        var live = _server.Connections;
        Connections.Clear();
        foreach (var c in live) Connections.Add(new FtpConnRowVm(c));
        ConnCountText = $"{live.Count} 个连接";
        OnPropertyChanged(nameof(NoConnections));
    }

    public void StartLive()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        RefreshState();
    }

    public void StopLive() => _timer?.Stop();

    private void OnTick(object? s, EventArgs e)
    {
        if (_server.Running && _server.StartedAt is DateTime t)
            UptimeText = FormatUptime(DateTime.Now - t);
        else UptimeText = "—";
        foreach (var c in Connections) c.Refresh();
    }

    private static string FormatUptime(TimeSpan up)
    {
        if (up.TotalSeconds < 0) up = TimeSpan.Zero;
        if (up.TotalDays >= 1) return $"{(int)up.TotalDays} 天 {up.Hours} 小时";
        if (up.TotalHours >= 1) return $"{(int)up.TotalHours} 小时 {up.Minutes} 分";
        if (up.TotalMinutes >= 1) return $"{(int)up.TotalMinutes} 分 {up.Seconds} 秒";
        return $"{(int)up.TotalSeconds} 秒";
    }

    private static IEnumerable<string> LocalIPv4()
    {
        var list = new List<string> { "127.0.0.1" };
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    list.Add(ip.ToString());
        }
        catch { /* best effort */ }
        return list.Distinct();
    }

    private static Dispatcher Dispatcher() => Application.Current.Dispatcher;

    private static void Error(string msg) => MessageBox.Show(msg, "FTP 服务端", MessageBoxButton.OK, MessageBoxImage.Warning);
}
