using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.ViewModels;

public sealed class FtpRemoteRowVm
{
    public FtpRemoteEntry Model { get; }
    public FtpRemoteRowVm(FtpRemoteEntry m) { Model = m; }
    public string Name => Model.Name;
    public bool IsDir => Model.IsDir;
    public string Icon => Model.IsDir ? "" : "";       // folder / document (Segoe MDL2)
    public string TypeText => Model.IsDir ? "文件夹" : "文件";
    public string SizeText => Model.IsDir ? "" : Human(Model.Size);
    public string ModifiedText => Model.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    internal static string Human(long b) => b >= 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024 / 1024:0.0} GB"
        : b >= 1024 * 1024 ? $"{b / 1024.0 / 1024:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";
}

public sealed class FtpLocalRowVm
{
    public FtpLocalRowVm(string path, bool isDir, bool isUp = false)
    {
        Path = path; IsDir = isDir; IsUp = isUp;
        Name = isUp ? ".." : System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name)) Name = path;   // drive root
        try { Size = isDir ? 0 : new FileInfo(path).Length; } catch { }
        try { Modified = File.GetLastWriteTime(path); } catch { }
    }
    public string Path { get; }
    public bool IsDir { get; }
    public bool IsUp { get; }
    public string Name { get; }
    public long Size { get; }
    public DateTime Modified { get; }
    public string Icon => IsUp ? "" : IsDir ? "" : "";
    public string TypeText => IsUp ? "上级" : IsDir ? "文件夹" : "文件";
    public string SizeText => IsDir ? "" : FtpRemoteRowVm.Human(Size);
    public string ModifiedText => IsUp ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>The 客户端 tab: connect to a remote FTP/FTPS server and transfer files between a local folder
/// (left) and the remote directory (right).</summary>
public sealed class FtpClientViewModel : ObservableObject
{
    private FtpClient? _client;
    private CancellationTokenSource? _cts;

    public FtpClientViewModel()
    {
        _localDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConnectCommand = new RelayCommand(_ => _ = ConnectAsync(), _ => !Connected && !Busy);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => Connected);
        RefreshRemoteCommand = new RelayCommand(_ => _ = ListRemoteAsync(), _ => Connected && !Busy);
        RemoteUpCommand = new RelayCommand(_ => _ = RemoteUpAsync(), _ => Connected && !Busy);
        OpenRemoteCommand = new RelayCommand(p => { if (p is FtpRemoteRowVm r) _ = OpenRemoteAsync(r); });
        DownloadCommand = new RelayCommand(_ => _ = DownloadAsync(), _ => Connected && !Busy && SelectedRemote != null);
        UploadCommand = new RelayCommand(_ => _ = UploadAsync(), _ => Connected && !Busy && SelectedLocal is { IsUp: false });
        DeleteRemoteCommand = new RelayCommand(_ => _ = DeleteRemoteAsync(), _ => Connected && !Busy && SelectedRemote != null);
        MkdirRemoteCommand = new RelayCommand(_ => _ = MkdirRemoteAsync(), _ => Connected && !Busy);
        RenameRemoteCommand = new RelayCommand(_ => _ = RenameRemoteAsync(), _ => Connected && !Busy && SelectedRemote != null);

        OpenLocalCommand = new RelayCommand(p => { if (p is FtpLocalRowVm r) OpenLocal(r); });
        LocalUpCommand = new RelayCommand(_ => LocalUp());
        PickLocalCommand = new RelayCommand(_ => PickLocal());
        ListLocal();
    }

    // ── connection form ────────────────────────────────────────────────────────
    private string _host = ""; public string Host { get => _host; set => Set(ref _host, value); }
    private int _port = 21; public int Port { get => _port; set => Set(ref _port, value); }
    private string _userName = ""; public string UserName { get => _userName; set => Set(ref _userName, value); }
    private string _password = ""; public string Password { get => _password; set => Set(ref _password, value); }

    private string _tlsMode = "explicit";
    public bool IsTlsNone { get => _tlsMode == "none"; set { if (value) SetTls("none"); } }
    public bool IsTlsExplicit { get => _tlsMode == "explicit"; set { if (value) SetTls("explicit"); } }
    public bool IsTlsImplicit { get => _tlsMode == "implicit"; set { if (value) SetTls("implicit"); } }
    private void SetTls(string m)
    {
        _tlsMode = m;
        OnPropertyChanged(nameof(IsTlsNone)); OnPropertyChanged(nameof(IsTlsExplicit)); OnPropertyChanged(nameof(IsTlsImplicit));
        if (m == "implicit" && _port == 21) Port = 990;
        else if (m != "implicit" && _port == 990) Port = 21;
    }

    // ── state ────────────────────────────────────────────────────────────────
    private bool _connected; public bool Connected { get => _connected; private set { if (Set(ref _connected, value)) { OnPropertyChanged(nameof(StatusBrush)); OnPropertyChanged(nameof(StatusText)); Requery(); } } }
    private bool _busy; public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(StatusText)); Requery(); } } }
    public string StatusText => _connected ? $"已连接 {Host}" : _busy ? "连接中…" : "未连接";
    public string StatusBrush => _connected ? "OkFg" : "TextTertiary";

    private string _note = "填写远端服务器信息并连接。支持明文 FTP、显式 FTPS (AUTH TLS)、隐式 FTPS。";
    public string Note { get => _note; set => Set(ref _note, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    // ── remote / local listings ──────────────────────────────────────────────
    public ObservableCollection<FtpRemoteRowVm> RemoteEntries { get; } = new();
    public ObservableCollection<FtpLocalRowVm> LocalEntries { get; } = new();

    private string _remoteDir = "/"; public string RemoteDir { get => _remoteDir; private set => Set(ref _remoteDir, value); }
    private string _localDir; public string LocalDir { get => _localDir; private set => Set(ref _localDir, value); }

    private FtpRemoteRowVm? _selectedRemote;
    public FtpRemoteRowVm? SelectedRemote { get => _selectedRemote; set { if (Set(ref _selectedRemote, value)) Requery(); } }
    private FtpLocalRowVm? _selectedLocal;
    public FtpLocalRowVm? SelectedLocal { get => _selectedLocal; set { if (Set(ref _selectedLocal, value)) Requery(); } }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshRemoteCommand { get; }
    public RelayCommand RemoteUpCommand { get; }
    public RelayCommand OpenRemoteCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand DeleteRemoteCommand { get; }
    public RelayCommand MkdirRemoteCommand { get; }
    public RelayCommand RenameRemoteCommand { get; }
    public RelayCommand OpenLocalCommand { get; }
    public RelayCommand LocalUpCommand { get; }
    public RelayCommand PickLocalCommand { get; }

    // ── connection ───────────────────────────────────────────────────────────
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host)) { Note = "请填写主机地址。"; return; }
        Busy = true;
        Note = "正在连接 …";
        var client = new FtpClient();
        client.Log += AppendLog;
        _cts = new CancellationTokenSource();
        try
        {
            await client.ConnectAsync(Host.Trim(), Port, _tlsMode, UserName, Password, _cts.Token);
            // Confirm the data channel actually works (initial listing) BEFORE declaring connected: a wrong
            // encryption mode often logs in over the control channel but then stalls/fails on the data
            // connection — marking 已连接 at that point would mislead the user.
            var entries = await client.ListAsync(_cts.Token);
            _client = client;
            RemoteDir = client.CurrentDir;
            RemoteEntries.Clear();
            foreach (var e in entries) RemoteEntries.Add(new FtpRemoteRowVm(e));
            OnPropertyChanged(nameof(NoRemote));
            Connected = true;
            AuditLog.Action($"FTP 客户端连接 {Host}:{Port} · TLS {_tlsMode}");
            Note = $"已连接到 {Host}。";
        }
        catch (Exception ex)
        {
            try { client.Dispose(); } catch { }
            _client = null;
            Connected = false;
            RemoteEntries.Clear();
            OnPropertyChanged(nameof(NoRemote));
            Note = "连接失败：" + (ex is OperationCanceledException ? "连接超时或被取消" : ex.Message)
                   + "（请检查主机、端口与加密方式是否与服务器一致）";
        }
        finally { Busy = false; }
    }

    private void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_client != null) _ = _client.QuitAsync(CancellationToken.None); } catch { }
        _client?.Dispose();
        _client = null;
        Connected = false;
        RemoteEntries.Clear();
        OnPropertyChanged(nameof(NoRemote));
        Note = "已断开连接。";
    }

    public bool NoRemote => RemoteEntries.Count == 0;

    // ── remote browsing ──────────────────────────────────────────────────────
    private async Task ListRemoteAsync()
    {
        if (_client == null) return;
        Busy = true;
        try
        {
            var entries = await _client.ListAsync(_cts!.Token);
            RemoteEntries.Clear();
            foreach (var e in entries) RemoteEntries.Add(new FtpRemoteRowVm(e));
            RemoteDir = _client.CurrentDir;
            OnPropertyChanged(nameof(NoRemote));
        }
        catch (Exception ex) { Note = "列目录失败：" + ex.Message; }
        finally { Busy = false; }
    }

    private async Task OpenRemoteAsync(FtpRemoteRowVm row)
    {
        if (_client == null || Busy) return;
        if (!row.IsDir) { await DownloadAsync(); return; }
        Busy = true;
        try { await _client.ChangeDirAsync(row.Name, _cts!.Token); }
        catch (Exception ex) { Note = "进入目录失败：" + ex.Message; Busy = false; return; }
        Busy = false;
        await ListRemoteAsync();
    }

    private async Task RemoteUpAsync()
    {
        if (_client == null) return;
        Busy = true;
        try { await _client.UpAsync(_cts!.Token); }
        catch (Exception ex) { Note = "返回上级失败：" + ex.Message; Busy = false; return; }
        Busy = false;
        await ListRemoteAsync();
    }

    // ── transfers ────────────────────────────────────────────────────────────
    private async Task DownloadAsync()
    {
        if (_client == null || SelectedRemote is not { } r) return;
        Busy = true;
        try
        {
            if (r.IsDir)
            {
                var n = 0;
                var p = new Progress<string>(name => { n++; Note = $"下载文件夹 {r.Name} … 第 {n} 个：{name}"; });
                await _client.DownloadDirectoryAsync(r.Name, LocalDir, p, _cts!.Token);
                AuditLog.Action($"FTP 下载文件夹 {r.Name} → {LocalDir}（{n} 个文件）");
                Note = $"已下载文件夹 {r.Name}（{n} 个文件）→ {LocalDir}";
            }
            else
            {
                var local = Path.Combine(LocalDir, r.Name);
                var p = new Progress<long>(b => Note = $"下载 {r.Name} … {FtpRemoteRowVm.Human(b)}");
                await _client.DownloadAsync(r.Name, local, p, _cts!.Token);
                AuditLog.Action($"FTP 下载 {r.Name} → {local}");
                Note = $"已下载 {r.Name} → {LocalDir}";
            }
            ListLocal();
        }
        catch (Exception ex) { Note = "下载失败：" + ex.Message; }
        finally { Busy = false; }
    }

    private async Task UploadAsync()
    {
        if (_client == null || SelectedLocal is not { IsUp: false } l) return;
        Busy = true;
        try
        {
            if (l.IsDir)
            {
                var n = 0;
                var p = new Progress<string>(name => { n++; Note = $"上传文件夹 {l.Name} … 第 {n} 个：{name}"; });
                await _client.UploadDirectoryAsync(l.Path, l.Name, p, _cts!.Token);
                AuditLog.Action($"FTP 上传文件夹 {l.Path} → {RemoteDir}/{l.Name}（{n} 个文件）");
                Note = $"已上传文件夹 {l.Name}（{n} 个文件）→ {RemoteDir}";
            }
            else
            {
                var p = new Progress<long>(b => Note = $"上传 {l.Name} … {FtpRemoteRowVm.Human(b)}");
                await _client.UploadAsync(l.Path, l.Name, p, _cts!.Token);
                AuditLog.Action($"FTP 上传 {l.Path} → {RemoteDir}/{l.Name}");
                Note = $"已上传 {l.Name} → {RemoteDir}";
            }
            await ListRemoteAsync();
        }
        catch (Exception ex) { Note = "上传失败：" + ex.Message; }
        finally { Busy = false; }
    }

    private async Task DeleteRemoteAsync()
    {
        if (_client == null || SelectedRemote is not { } r) return;
        if (MessageBox.Show($"删除远端 {(r.IsDir ? "目录" : "文件")} {r.Name}？", "删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Busy = true;
        try
        {
            if (r.IsDir) await _client.RemoveDirAsync(r.Name, _cts!.Token);
            else await _client.DeleteAsync(r.Name, _cts!.Token);
            AuditLog.Action($"FTP 删除远端 {r.Name}");
            Note = $"已删除 {r.Name}";
        }
        catch (Exception ex) { Note = "删除失败：" + ex.Message; }
        finally { Busy = false; }
        await ListRemoteAsync();
    }

    private async Task MkdirRemoteAsync()
    {
        if (_client == null) return;
        var dlg = new Views.InputDialog("新建远端目录", "目录名：", "new_folder") { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Busy = true;
        try { await _client.MakeDirAsync(dlg.Value, _cts!.Token); Note = $"已创建目录 {dlg.Value}"; }
        catch (Exception ex) { Note = "创建失败：" + ex.Message; }
        finally { Busy = false; }
        await ListRemoteAsync();
    }

    private async Task RenameRemoteAsync()
    {
        if (_client == null || SelectedRemote is not { } r) return;
        var dlg = new Views.InputDialog("重命名", $"将 {r.Name} 重命名为：", r.Name, r.Name) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Value == r.Name) return;
        Busy = true;
        try { await _client.RenameAsync(r.Name, dlg.Value, _cts!.Token); Note = $"已重命名为 {dlg.Value}"; }
        catch (Exception ex) { Note = "重命名失败：" + ex.Message; }
        finally { Busy = false; }
        await ListRemoteAsync();
    }

    // ── local browsing ───────────────────────────────────────────────────────
    private void ListLocal()
    {
        LocalEntries.Clear();
        try
        {
            var parent = Directory.GetParent(LocalDir);
            if (parent != null) LocalEntries.Add(new FtpLocalRowVm(parent.FullName, true, isUp: true));
            foreach (var d in Directory.GetDirectories(LocalDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                LocalEntries.Add(new FtpLocalRowVm(d, true));
            foreach (var f in Directory.GetFiles(LocalDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                LocalEntries.Add(new FtpLocalRowVm(f, false));
        }
        catch (Exception ex) { Note = "读取本地目录失败：" + ex.Message; }
    }

    private void OpenLocal(FtpLocalRowVm row)
    {
        if (row.IsDir) { LocalDir = row.Path; ListLocal(); }
        else SelectedLocal = row;
    }

    private void LocalUp()
    {
        var parent = Directory.GetParent(LocalDir);
        if (parent != null) { LocalDir = parent.FullName; ListLocal(); }
    }

    private void PickLocal()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "选择本地目录", InitialDirectory = LocalDir };
        if (d.ShowDialog() == true) { LocalDir = d.FolderName; ListLocal(); }
    }

    private void AppendLog(string line)
        => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LogText = (LogText.Length > 12000 ? LogText[^9000..] : LogText) + line + "\n";
        });

    private static void Requery() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}
