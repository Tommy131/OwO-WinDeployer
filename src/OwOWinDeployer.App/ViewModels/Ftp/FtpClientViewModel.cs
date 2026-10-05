using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using OwOWinDeployer.App;
using OwOWinDeployer.App.Services;
using OwOWinDeployer.App.Services.Ftp;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.ViewModels.Ftp;

public sealed class FtpRemoteRowVm : ObservableObject
{
    public FtpRemoteEntry Model { get; }
    public FtpRemoteRowVm(FtpRemoteEntry m) { Model = m; }
    public string Name => Model.Name;
    public bool IsDir => Model.IsDir;
    public string Icon => Model.IsDir ? "" : "";       // folder / document (Segoe MDL2)
    public string TypeText => Model.IsDir ? Localizer.T("ftp.client.typeDir") : Localizer.T("ftp.client.typeFile");
    public string SizeText => Model.IsDir ? "" : Human(Model.Size);
    public string ModifiedText => Model.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";

    // Per-entry permission gating from the MLSD `perm` fact. When the server reports no perm (empty),
    // assume allowed — a generic FTP server may not advertise perms and we shouldn't false-disable.
    private bool HasPerm(char c) => Model.Perm.Length == 0 || Model.Perm.IndexOf(c) >= 0;
    public bool CanDownload => Model.IsDir ? (HasPerm('e') || HasPerm('l')) : HasPerm('r');
    public bool CanRename => HasPerm('f');
    public bool CanDelete => HasPerm('d');

    internal static string Human(long b) => b >= 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024 / 1024:0.0} GB"
        : b >= 1024 * 1024 ? $"{b / 1024.0 / 1024:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";
}

public sealed class FtpLocalRowVm : ObservableObject
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
    public string TypeText => IsUp ? Localizer.T("ftp.client.typeUp") : IsDir ? Localizer.T("ftp.client.typeDir") : Localizer.T("ftp.client.typeFile");
    public string SizeText => IsDir ? "" : FtpRemoteRowVm.Human(Size);
    public string ModifiedText => IsUp ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>The 客户端 tab: connect to a remote FTP/FTPS server and transfer files between a local folder
/// (left) and the remote directory (right).</summary>
public sealed class FtpClientViewModel : LocalizedObject
{
    private FtpClient? _client;
    private CancellationTokenSource? _cts;
    private FtpConnectionSnapshot? _connectionSnapshot;
    private FtpTransferSessionFactory? _transferSessions;
    private FtpTransferExecutor? _transferExecutor;
    private FtpTransferCoordinator? _transferCoordinator;
    private FtpTransferPlanner? _transferPlanner;
    private readonly FtpLogBuffer _logBuffer = new();
    private readonly ConcurrentDictionary<Guid, FtpTransferJob> _dirtyTransferJobs = new();
    private readonly List<CancellationTokenSource> _planningCancellations = new();
    private readonly object _planningGate = new();
    private TaskCompletionSource _planningIdle = CompletedSource();
    private readonly Dictionary<Guid, FtpTransferRowViewModel> _transferRowsById = new();
    private readonly HashSet<Guid> _hiddenTransferIds = new();
    private readonly System.Windows.Threading.DispatcherTimer _transferUiTimer;
    private int _planningCount;
    private bool _disconnecting;

    public FtpClientViewModel()
    {
        _localDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConnectCommand = new RelayCommand(_ => _ = ConnectAsync(), _ => !Connected && !Busy);
        DisconnectCommand = new RelayCommand(_ => _ = DisconnectAsync(), _ => Connected && !_disconnecting);
        RefreshRemoteCommand = new RelayCommand(_ => _ = ListRemoteAsync(), _ => Connected && !Busy);
        RemoteUpCommand = new RelayCommand(_ => _ = RemoteUpAsync(), _ => Connected && !Busy);
        OpenRemoteCommand = new RelayCommand(p => { if (p is FtpRemoteRowVm r) _ = OpenRemoteAsync(r); });
        DownloadCommand = new RelayCommand(_ => _ = DownloadAsync(), _ => Connected && !Busy &&
            (_selRemotes.Count > 0 ? _selRemotes.All(r => r.CanDownload) : SelectedRemote is { CanDownload: true }));
        UploadCommand = new RelayCommand(_ => _ = UploadAsync(), _ => Connected && !Busy && (_selLocals.Count > 0 || SelectedLocal is { IsUp: false }));
        DeleteRemoteCommand = new RelayCommand(_ => _ = DeleteRemoteAsync(), _ => Connected && !Busy &&
            (_selRemotes.Count > 0 ? _selRemotes.All(r => r.CanDelete) : SelectedRemote is { CanDelete: true }));
        MkdirRemoteCommand = new RelayCommand(_ => _ = MkdirRemoteAsync(), _ => Connected && !Busy);
        RenameRemoteCommand = new RelayCommand(_ => _ = RenameRemoteAsync(), _ => Connected && !Busy && _selRemotes.Count <= 1 && SelectedRemote is { CanRename: true });
        OpenRemoteItemCommand = new RelayCommand(_ => _ = OpenRemoteItemAsync(), _ => Connected && !Busy && SelectedRemote is { CanDownload: true });

        OpenLocalCommand = new RelayCommand(p => { if (p is FtpLocalRowVm r) OpenLocal(r); });
        OpenLocalItemCommand = new RelayCommand(_ => OpenLocalItem(), _ => SelectedLocal is { IsUp: false });
        RenameLocalCommand = new RelayCommand(_ => RenameLocal(), _ => !Busy && _selLocals.Count <= 1 && SelectedLocal is { IsUp: false });
        DeleteLocalCommand = new RelayCommand(_ => DeleteLocal(), _ => !Busy && (_selLocals.Count > 0 || SelectedLocal is { IsUp: false }));
        LocalUpCommand = new RelayCommand(_ => LocalUp());
        PickLocalCommand = new RelayCommand(_ => PickLocal());

        SaveProfileCommand = new RelayCommand(_ => SaveProfile());
        DeleteProfileCommand = new RelayCommand(_ => DeleteProfile(), _ => SelectedProfile != null);
        CancelTransferCommand = new RelayCommand(p => { if (p is FtpTransferRowViewModel row) CancelTransfer(row); },
            p => p is FtpTransferRowViewModel { CanCancel: true });
        CancelAllTransfersCommand = new RelayCommand(_ => CancelAllTransfers(), _ => HasActiveTransfers);
        ClearFinishedTransfersCommand = new RelayCommand(_ => ClearFinishedTransfers(),
            _ => TransferRows.Any(x => x.State is FtpTransferState.Completed or FtpTransferState.Canceled));
        ToggleTransferQueueCommand = new RelayCommand(_ => QueueExpanded = !QueueExpanded, _ => HasTransfers);
        LoadProfiles();
        ListLocal();

        _transferUiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _transferUiTimer.Tick += (_, _) => RefreshTransferUi();
        _transferUiTimer.Start();
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
    public string StatusText => _connected ? Localizer.Format("ftp.client.connected", Host) : _busy ? Localizer.T("ftp.client.connecting") : Localizer.T("ftp.client.notConnected");
    public string StatusBrush => _connected ? "OkFg" : "TextTertiary";
    private string _connectedDisplayName = "";
    public string ConnectedTitle => Localizer.Format("ftp.client.connectedAs", _connectedDisplayName);
    public string ConnectedDetails => _connectionSnapshot == null ? "" : Localizer.Format("ftp.client.connectedDetails",
        string.IsNullOrWhiteSpace(_connectionSnapshot.UserName) ? Localizer.T("ftp.client.anonymous") : _connectionSnapshot.UserName,
        _connectionSnapshot.Host, _connectionSnapshot.Port, TlsText(_connectionSnapshot.TlsMode));

    private string _note = Localizer.T("ftp.client.note");
    public string Note { get => _note; set => Set(ref _note, value); }

    private string _logText = "";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    // ── background transfer queue ─────────────────────────────────────────────
    public ObservableCollection<FtpTransferRowViewModel> TransferRows { get; } = new();
    private bool _hasTransfers; public bool HasTransfers { get => _hasTransfers; private set => Set(ref _hasTransfers, value); }
    private bool _hasActiveTransfers; public bool HasActiveTransfers { get => _hasActiveTransfers; private set { if (Set(ref _hasActiveTransfers, value)) Requery(); } }
    private bool _queueExpanded = true; public bool QueueExpanded { get => _queueExpanded; set => Set(ref _queueExpanded, value); }
    private string _transferSummary = ""; public string TransferSummary { get => _transferSummary; private set => Set(ref _transferSummary, value); }
    private string _aggregateSpeedText = "—"; public string AggregateSpeedText { get => _aggregateSpeedText; private set => Set(ref _aggregateSpeedText, value); }
    private int _fileConcurrency = 4;
    public int FileConcurrency
    {
        get => _fileConcurrency;
        set
        {
            var normalized = Math.Clamp(value, 1, 8);
            if (!Set(ref _fileConcurrency, normalized)) return;
            _transferCoordinator?.SetMaxConcurrentFiles(normalized);
        }
    }
    public IReadOnlyList<int> ConcurrencyOptions { get; } = Enumerable.Range(1, 8).ToArray();

    // ── remote / local listings ──────────────────────────────────────────────
    public ObservableCollection<FtpRemoteRowVm> RemoteEntries { get; } = new();
    public ObservableCollection<FtpLocalRowVm> LocalEntries { get; } = new();

    private string _remoteDir = "/"; public string RemoteDir { get => _remoteDir; private set => Set(ref _remoteDir, value); }
    private string _localDir; public string LocalDir { get => _localDir; private set => Set(ref _localDir, value); }

    private FtpRemoteRowVm? _selectedRemote;
    public FtpRemoteRowVm? SelectedRemote { get => _selectedRemote; set { if (Set(ref _selectedRemote, value)) Requery(); } }
    private FtpLocalRowVm? _selectedLocal;
    public FtpLocalRowVm? SelectedLocal { get => _selectedLocal; set { if (Set(ref _selectedLocal, value)) Requery(); } }

    // Multi-selection for batch transfer; kept in sync by the view's SelectionChanged handlers.
    private IReadOnlyList<FtpRemoteRowVm> _selRemotes = Array.Empty<FtpRemoteRowVm>();
    private IReadOnlyList<FtpLocalRowVm> _selLocals = Array.Empty<FtpLocalRowVm>();
    public void SetRemoteSelection(System.Collections.IList items) { _selRemotes = items.OfType<FtpRemoteRowVm>().ToList(); Requery(); }
    public void SetLocalSelection(System.Collections.IList items) { _selLocals = items.OfType<FtpLocalRowVm>().Where(x => !x.IsUp).ToList(); Requery(); }

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
    public RelayCommand OpenRemoteItemCommand { get; }
    public RelayCommand OpenLocalItemCommand { get; }
    public RelayCommand RenameLocalCommand { get; }
    public RelayCommand DeleteLocalCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand CancelTransferCommand { get; }
    public RelayCommand CancelAllTransfersCommand { get; }
    public RelayCommand ClearFinishedTransfersCommand { get; }
    public RelayCommand ToggleTransferQueueCommand { get; }

    // ── saved credentials (site manager) ───────────────────────────────────────
    public ObservableCollection<FtpClientProfile> Profiles { get; } = new();
    public bool HasProfiles => Profiles.Count > 0;

    private FtpClientProfile? _selectedProfile;
    public FtpClientProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value)) { Requery(); if (value != null) ApplyProfile(value); } }
    }

    /// <summary>Raised when a saved profile is applied, so the view can push the password into its PasswordBox.</summary>
    public event Action<string>? PasswordFilled;

    // ── connection ───────────────────────────────────────────────────────────
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host)) { Note = Localizer.T("ftp.client.requireHost"); return; }
        Busy = true;
        Note = Localizer.T("ftp.client.connectingNote");
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
            _connectedDisplayName = SelectedProfile is { } profile &&
                                    profile.Host.Equals(Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                    profile.Port == Port
                ? profile.Name
                : Localizer.Format("ftp.client.profileDefaultName",
                    string.IsNullOrWhiteSpace(UserName) ? Localizer.T("ftp.client.anonymous") : UserName,
                    Host.Trim(), Port);
            _connectionSnapshot = new FtpConnectionSnapshot(Host.Trim(), Port, _tlsMode, UserName, Password,
                _connectedDisplayName);
            _transferSessions = new FtpTransferSessionFactory();
            _transferSessions.Log += AppendLog;
            _transferExecutor = new FtpTransferExecutor(_connectionSnapshot, _transferSessions);
            _transferCoordinator = new FtpTransferCoordinator(_transferExecutor, FileConcurrency);
            _transferCoordinator.JobChanged += OnTransferJobChanged;
            _transferPlanner = new FtpTransferPlanner(_transferSessions);
            Connected = true;
            OnPropertyChanged(nameof(ConnectedTitle));
            OnPropertyChanged(nameof(ConnectedDetails));
            AuditLog.Action($"FTP 客户端连接 {Host}:{Port} · TLS {_tlsMode}");
            Note = Localizer.Format("ftp.client.connectedNote", Host);
        }
        catch (Exception ex)
        {
            // Check whether the failure was a TOFU cert-change rejection before disposing the client,
            // because PendingCertThumbprint lives on the FtpClient instance.
            var pendingThumb = client.PendingCertThumbprint;
            try { client.Dispose(); } catch { }
            _client = null;
            Connected = false;
            RemoteEntries.Clear();
            OnPropertyChanged(nameof(NoRemote));

            if (pendingThumb != null && !(ex is OperationCanceledException))
            {
                // Offer to re-trust the new cert and retry once.
                var hostKey = $"{Host.Trim()}:{Port}";
                var msg = Localizer.Format("ftp.client.certChangedRetrust", Host.Trim(), pendingThumb[..16]);
                var result = Dialogs.Show(msg, Localizer.T("ftp.client.certChangedTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    FtpTrustStore.Remove(hostKey);
                    Busy = false;
                    _ = ConnectAsync();   // retry — new FtpClient will re-pin the new cert
                    return;
                }
            }

            Note = Localizer.Format("ftp.client.connectFailed",
                ex is OperationCanceledException ? Localizer.T("ftp.client.connectFailedCanceled") : ex.Message);
        }
        finally { Busy = false; }
    }

    private async Task DisconnectAsync()
    {
        if (_disconnecting) return;
        if (HasActiveTransfers && Dialogs.Show(Localizer.T("ftp.transfer.disconnectConfirm"),
                Localizer.T("ftp.transfer.disconnectTitle"), MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _disconnecting = true;
        Requery();
        try
        {
            await StopTransfersAsync();
            try { _cts?.Cancel(); } catch { }
            try { if (_client != null) _ = _client.QuitAsync(CancellationToken.None); } catch { }
            _client?.Dispose();
            _client = null;
            _cts?.Dispose();
            _cts = null;
            Connected = false;
            RemoteEntries.Clear();
            OnPropertyChanged(nameof(NoRemote));
            Note = Localizer.T("ftp.client.disconnected");
        }
        finally
        {
            _disconnecting = false;
            Requery();
        }
    }

    private async Task StopTransfersAsync()
    {
        CancelPlanning();
        await WaitForPlanningAsync().ConfigureAwait(true);
        var coordinator = _transferCoordinator;
        coordinator?.CancelAll();
        if (coordinator != null)
        {
            try { await coordinator.DisposeAsync(); } catch { }
        }
        RefreshTransferUi();
        if (coordinator != null) coordinator.JobChanged -= OnTransferJobChanged;
        _transferCoordinator = null;
        if (_transferSessions != null) _transferSessions.Log -= AppendLog;
        var executor = _transferExecutor;
        _transferExecutor = null;
        DisposeExecutorAfterWorkers(coordinator, executor);
        _transferSessions = null;
        _transferPlanner = null;
        _connectionSnapshot = null;
        OnPropertyChanged(nameof(ConnectedTitle));
        OnPropertyChanged(nameof(ConnectedDetails));
    }

    public void Shutdown()
    {
        _transferUiTimer.Stop();
        CancelPlanning();
        try { WaitForPlanningAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch { }
        var coordinator = _transferCoordinator;
        _transferCoordinator = null;
        coordinator?.CancelAll();
        if (coordinator != null) coordinator.JobChanged -= OnTransferJobChanged;
        if (coordinator != null)
        {
            try { coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
        if (_transferSessions != null) _transferSessions.Log -= AppendLog;
        var executor = _transferExecutor;
        _transferExecutor = null;
        DisposeExecutorAfterWorkers(coordinator, executor);
        _transferSessions = null;
        _transferPlanner = null;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _client?.Dispose();
        _client = null;
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
        catch (Exception ex) { Note = Localizer.Format("ftp.client.listFailed", ex.Message); }
        finally { Busy = false; }
    }

    private async Task OpenRemoteAsync(FtpRemoteRowVm row)
    {
        if (_client == null || Busy) return;
        if (!row.IsDir) { await DownloadAsync(); return; }
        Busy = true;
        try { await _client.ChangeDirAsync(row.Name, _cts!.Token); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.enterDirFailed", ex.Message); Busy = false; return; }
        Busy = false;
        await ListRemoteAsync();
    }

    private async Task RemoteUpAsync()
    {
        if (_client == null) return;
        Busy = true;
        try { await _client.UpAsync(_cts!.Token); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.upFailed", ex.Message); Busy = false; return; }
        Busy = false;
        await ListRemoteAsync();
    }

    // ── background transfers ──────────────────────────────────────────────────
    private async Task DownloadAsync()
    {
        if (_transferCoordinator == null || _transferPlanner == null || _connectionSnapshot == null) return;
        var items = BatchRemote();
        if (items.Count == 0) return;
        var coordinator = _transferCoordinator;
        var planner = _transferPlanner;
        var connection = _connectionSnapshot;
        var remoteDirectory = RemoteDir;
        var localDirectory = LocalDir;
        var batchId = Guid.NewGuid();
        using var planning = RegisterPlanning();
        QueueExpanded = true;
        Note = Localizer.T("ftp.transfer.scanning");
        var queued = 0;
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var request in planner.PlanDownloadsAsync(connection,
                                   items.Select(x => x.Model).ToList(), remoteDirectory, localDirectory,
                                   batchId, planning.Token))
                {
                    coordinator.Enqueue(new[] { request });
                    queued++;
                }
            }, planning.Token);
            Note = Localizer.Format("ftp.transfer.queuedCount", queued);
            await coordinator.WhenIdleAsync(planning.Token);
            AuditLog.Action($"FTP 下载批次完成 · {queued} 项 → {localDirectory}");
            ListLocal();
        }
        catch (OperationCanceledException) { Note = Localizer.T("ftp.transfer.allCanceled"); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.downloadFailed", ex.Message); }
        finally { UnregisterPlanning(planning); }
    }

    private async Task UploadAsync()
    {
        if (_transferCoordinator == null || _transferPlanner == null) return;
        var items = BatchLocal();
        if (items.Count == 0) return;
        var coordinator = _transferCoordinator;
        var planner = _transferPlanner;
        var remoteDirectory = RemoteDir;
        var batchId = Guid.NewGuid();
        using var planning = RegisterPlanning();
        QueueExpanded = true;
        Note = Localizer.T("ftp.transfer.scanning");
        var queued = 0;
        try
        {
            await Task.Run(async () =>
            {
                await foreach (var request in planner.PlanUploadsAsync(items.Select(x => x.Path).ToList(),
                                   remoteDirectory, batchId, planning.Token))
                {
                    coordinator.Enqueue(new[] { request });
                    queued++;
                }
            }, planning.Token);
            Note = Localizer.Format("ftp.transfer.queuedCount", queued);
            await coordinator.WhenIdleAsync(planning.Token);
            AuditLog.Action($"FTP 上传批次完成 · {queued} 项 → {remoteDirectory}");
            if (Connected && !Busy) await ListRemoteAsync();
        }
        catch (OperationCanceledException) { Note = Localizer.T("ftp.transfer.allCanceled"); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.uploadFailed", ex.Message); }
        finally { UnregisterPlanning(planning); }
    }

    private CancellationTokenSource RegisterPlanning()
    {
        var cancellation = _cts == null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_planningGate)
        {
            if (Interlocked.Increment(ref _planningCount) == 1) _planningIdle = NewSource();
            _planningCancellations.Add(cancellation);
        }
        return cancellation;
    }

    private void UnregisterPlanning(CancellationTokenSource cancellation)
    {
        lock (_planningGate)
        {
            _planningCancellations.Remove(cancellation);
            if (Interlocked.Decrement(ref _planningCount) == 0) _planningIdle.TrySetResult();
        }
    }

    private Task WaitForPlanningAsync()
    {
        lock (_planningGate) return _planningIdle.Task;
    }

    private static void DisposeExecutorAfterWorkers(FtpTransferCoordinator? coordinator, FtpTransferExecutor? executor)
    {
        if (executor == null) return;
        if (coordinator == null || coordinator.WorkersCompletion.IsCompleted)
        {
            executor.Dispose();
            return;
        }
        _ = coordinator.WorkersCompletion.ContinueWith(_ => executor.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static TaskCompletionSource NewSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        var source = NewSource();
        source.SetResult();
        return source;
    }

    private void CancelPlanning()
    {
        CancellationTokenSource[] cancellations;
        lock (_planningGate) cancellations = _planningCancellations.ToArray();
        foreach (var cancellation in cancellations)
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void CancelTransfer(FtpTransferRowViewModel row)
    {
        _transferCoordinator?.Cancel(row.Id);
        RefreshTransferUi();
    }

    private void CancelAllTransfers()
    {
        CancelPlanning();
        _transferCoordinator?.CancelAll();
        Note = Localizer.T("ftp.transfer.allCanceling");
        RefreshTransferUi();
    }

    private void ClearFinishedTransfers()
    {
        foreach (var row in TransferRows
                     .Where(x => x.State is FtpTransferState.Completed or FtpTransferState.Canceled)
                     .ToList())
        {
            _hiddenTransferIds.Add(row.Id);
            _transferRowsById.Remove(row.Id);
            TransferRows.Remove(row);
            _transferCoordinator?.Remove(row.Id);
        }
        RefreshTransferUi();
    }

    private void RefreshTransferUi()
    {
        FlushPendingLogLines();
        foreach (var pair in _dirtyTransferJobs.ToArray())
        {
            if (!_dirtyTransferJobs.TryRemove(pair.Key, out var job)) continue;
            if (_hiddenTransferIds.Contains(job.Id)) continue;
            if (!_transferRowsById.TryGetValue(job.Id, out var row))
            {
                row = new FtpTransferRowViewModel(job);
                _transferRowsById.Add(job.Id, row);
                TransferRows.Add(row);
            }
            row.Refresh();
        }

        var planning = Volatile.Read(ref _planningCount);
        var active = planning + TransferRows.Count(x => x.State is FtpTransferState.Connecting
            or FtpTransferState.Transferring or FtpTransferState.Completing or FtpTransferState.Canceling);
        var queued = TransferRows.Count(x => x.State == FtpTransferState.Queued);
        var speed = TransferRows.Sum(x => x.BytesPerSecond);
        HasTransfers = planning > 0 || TransferRows.Count > 0;
        HasActiveTransfers = active > 0 || queued > 0;
        TransferSummary = Localizer.Format("ftp.transfer.summary", active, queued);
        AggregateSpeedText = speed > 0 ? FtpTransferRowViewModel.FormatSpeed(speed) : "—";
        Requery();
    }

    private void FlushPendingLogLines()
    {
        if (_logBuffer.TryTakeSnapshot(out var logText)) LogText = logText;
    }

    private static string TlsText(string mode) => Localizer.T(mode switch
    {
        "none" => "ftp.client.plaintext",
        "implicit" => "ftp.client.tlsImplicit",
        _ => "ftp.client.tlsExplicit",
    });

    // The right-clicked single row (Grid_RightSelect selects exactly one) or the multi-selection.
    private List<FtpRemoteRowVm> BatchRemote()
        => _selRemotes.Count > 0 ? _selRemotes.ToList()
         : SelectedRemote != null ? new List<FtpRemoteRowVm> { SelectedRemote } : new();

    private List<FtpLocalRowVm> BatchLocal()
        => _selLocals.Count > 0 ? _selLocals.ToList()
         : SelectedLocal is { IsUp: false } s ? new List<FtpLocalRowVm> { s } : new();

    private async Task DeleteRemoteAsync()
    {
        if (_client == null) return;
        var items = BatchRemote();
        if (items.Count == 0) return;
        var label = items.Count == 1
            ? Localizer.Format("ftp.client.deleteRemoteLabelOne", items[0].IsDir ? Localizer.T("ftp.client.typeDir") : Localizer.T("ftp.client.typeFile"), items[0].Name)
            : Localizer.Format("ftp.client.deleteRemoteLabelMany", items.Count);
        var dirNote = items.Any(x => x.IsDir) ? Localizer.T("ftp.client.deleteDirNote") : "";
        if (Dialogs.Show(Localizer.Format("ftp.client.deleteConfirm", label, dirNote), Localizer.T("ftp.client.deleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Busy = true;
        var ok = 0;
        try
        {
            foreach (var r in items)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                if (r.IsDir) await _client.DeleteDirectoryAsync(r.Name, _cts.Token);   // recursive: clears contents then RMD
                else await _client.DeleteAsync(r.Name, _cts.Token);
                ok++;
            }
            AuditLog.Action($"FTP 删除远端 {ok} 项");
            Note = Localizer.Format("ftp.client.deleted", ok);
        }
        catch (OperationCanceledException) { Note = Localizer.Format("ftp.client.deleteCanceled", ok); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.deleteFailed", ok, ex.Message); }
        finally { Busy = false; }
        await ListRemoteAsync();
    }

    private async Task MkdirRemoteAsync()
    {
        if (_client == null) return;
        var dlg = new InputDialog(Localizer.T("ftp.client.mkdirRemoteTitle"), Localizer.T("ftp.client.mkdirRemotePrompt"), Localizer.T("ftp.client.mkdirRemoteDefault")) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Busy = true;
        try { await _client.MakeDirAsync(dlg.Value, _cts!.Token); Note = Localizer.Format("ftp.client.mkdirDone", dlg.Value); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.mkdirFailed", ex.Message); }
        finally { Busy = false; }
        await ListRemoteAsync();
    }

    private async Task RenameRemoteAsync()
    {
        if (_client == null || SelectedRemote is not { } r) return;
        var dlg = new InputDialog(Localizer.T("ftp.client.renameTitle"), Localizer.Format("ftp.client.renamePrompt", r.Name), r.Name, r.Name) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Value == r.Name) return;
        Busy = true;
        try { await _client.RenameAsync(r.Name, dlg.Value, _cts!.Token); Note = Localizer.Format("ftp.client.renamed", dlg.Value); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.renameFailed", ex.Message); }
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
        catch (Exception ex) { Note = Localizer.Format("ftp.client.localDirReadFailed", ex.Message); }
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
        var d = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("ftp.client.pickLocalTitle"), InitialDirectory = LocalDir };
        if (d.ShowDialog() == true) { LocalDir = d.FolderName; ListLocal(); }
    }

    // ── saved credentials ──────────────────────────────────────────────────────
    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in FtpClientStore.Load()) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles));
    }

    /// <summary>Fill the connection form from a saved profile (decrypting its password) so the user can just
    /// click 连接.</summary>
    private void ApplyProfile(FtpClientProfile p)
    {
        Host = p.Host;
        _tlsMode = string.IsNullOrEmpty(p.TlsMode) ? "explicit" : p.TlsMode;
        OnPropertyChanged(nameof(IsTlsNone)); OnPropertyChanged(nameof(IsTlsExplicit)); OnPropertyChanged(nameof(IsTlsImplicit));
        Port = p.Port;
        UserName = p.UserName;
        Password = Dpapi.Unprotect(p.PasswordEnc);
        PasswordFilled?.Invoke(Password);
        Note = Localizer.Format("ftp.client.profileApplied", p.Name);
    }

    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(Host)) { Note = Localizer.T("ftp.client.profileNeedHost"); return; }
        var def = SelectedProfile?.Name ?? Localizer.Format("ftp.client.profileDefaultName", string.IsNullOrEmpty(UserName) ? Localizer.T("ftp.client.anonymous") : UserName, Host, Port);
        var dlg = new InputDialog(Localizer.T("ftp.client.profileSaveTitle"), Localizer.T("ftp.client.profileSavePrompt"), def, def) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Value.Length == 0) return;
        var name = dlg.Value;
        var prof = Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var isNew = prof == null;
        prof ??= new FtpClientProfile();
        prof.Name = name;
        prof.Host = Host.Trim();
        prof.Port = Port;
        prof.TlsMode = _tlsMode;
        prof.UserName = UserName;
        prof.PasswordEnc = Dpapi.Protect(Password);
        if (isNew) Profiles.Add(prof);
        FtpClientStore.Save(Profiles);
        _selectedProfile = prof; OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasProfiles));
        Requery();
        AuditLog.Action($"FTP 凭据保存：{name}");
        Note = Localizer.Format("ftp.client.profileSaved", name);
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is not { } p) return;
        if (Dialogs.Show(Localizer.Format("ftp.client.profileDeleteConfirm", p.Name), Localizer.T("ftp.client.profileDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Profiles.Remove(p);
        _selectedProfile = null; OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(HasProfiles));
        Requery();
        FtpClientStore.Save(Profiles);
        AuditLog.Action($"FTP 凭据删除：{p.Name}");
        Note = Localizer.Format("ftp.client.profileDeleted", p.Name);
    }

    // ── context-menu actions (local & remote, file or folder) ──────────────────
    /// <summary>Open a local file with its associated app, or a local folder in Explorer.</summary>
    private void OpenLocalItem()
    {
        if (SelectedLocal is not { IsUp: false } l) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(l.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.openFailed", ex.Message); }
    }

    private void RenameLocal()
    {
        if (SelectedLocal is not { IsUp: false } l) return;
        var dlg = new InputDialog(Localizer.T("ftp.client.renameTitle"), Localizer.Format("ftp.client.renamePrompt", l.Name), l.Name, l.Name) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Value.Length == 0 || dlg.Value == l.Name) return;
        try
        {
            var dest = Path.Combine(Path.GetDirectoryName(l.Path)!, dlg.Value);
            if (l.IsDir) Directory.Move(l.Path, dest); else File.Move(l.Path, dest);
            Note = Localizer.Format("ftp.client.renamed", dlg.Value);
            ListLocal();
        }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.renameFailed", ex.Message); }
    }

    private void DeleteLocal()
    {
        var items = BatchLocal();
        if (items.Count == 0) return;
        var label = items.Count == 1
            ? Localizer.Format("ftp.client.localDeleteLabelOne", items[0].IsDir ? Localizer.T("ftp.client.typeDir") : Localizer.T("ftp.client.typeFile"), items[0].Name)
            : Localizer.Format("ftp.client.localDeleteLabelMany", items.Count);
        var dirNote = items.Any(x => x.IsDir) ? Localizer.T("ftp.client.localDeleteDirNote") : "";
        if (Dialogs.Show(Localizer.Format("ftp.client.deleteConfirm", label, dirNote), Localizer.T("ftp.client.deleteTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ok = 0;
        try
        {
            foreach (var l in items)
            {
                if (l.IsDir) Directory.Delete(l.Path, true); else File.Delete(l.Path);
                ok++;
            }
            Note = Localizer.Format("ftp.client.deleted", ok);
        }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.deleteFailed", ok, ex.Message); }
        ListLocal();
    }

    /// <summary>Open a remote folder (enter it) or a remote file (download to temp, then open locally).</summary>
    private async Task OpenRemoteItemAsync()
    {
        if (_client == null || SelectedRemote is not { } r) return;
        if (r.IsDir) { await OpenRemoteAsync(r); return; }
        if (_transferCoordinator == null || _connectionSnapshot == null) return;
        var coordinator = _transferCoordinator;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "OwOWinDeployerFtp");
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + "-" + FtpTransferPlanner.SafeLocalName(r.Name));
            Note = Localizer.Format("ftp.client.openingRemote", r.Name);
            QueueExpanded = true;
            var request = new FtpTransferRequest(FtpTransferDirection.Download,
                FtpTransferPlanner.CombineRemote(RemoteDir, r.Name), tmp, r.Model.Size, Guid.NewGuid());
            var job = coordinator.Enqueue(new[] { request }).Single();
            await job.Completion;
            if (job.State != FtpTransferState.Completed)
            {
                Note = job.State == FtpTransferState.Canceled
                    ? Localizer.T("ftp.transfer.canceled")
                    : Localizer.Format("ftp.client.openFailed", job.Error ?? Localizer.T("ftp.transfer.failedGeneric"));
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tmp) { UseShellExecute = true });
            Note = Localizer.Format("ftp.client.openedRemote", r.Name);
        }
        catch (Exception ex) { Note = Localizer.Format("ftp.client.openFailed", ex.Message); }
    }

    private void AppendLog(string line) => _logBuffer.Append(line);

    private void OnTransferJobChanged(FtpTransferJob job) => _dirtyTransferJobs[job.Id] = job;

    private static void Requery() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var r in RemoteEntries) r.RaiseAllPropertiesChanged();
        foreach (var l in LocalEntries) l.RaiseAllPropertiesChanged();
        foreach (var row in TransferRows) row.RaiseAllPropertiesChanged();
        OnPropertyChanged(nameof(ConnectedTitle));
        OnPropertyChanged(nameof(ConnectedDetails));
        RefreshTransferUi();
    }
}
