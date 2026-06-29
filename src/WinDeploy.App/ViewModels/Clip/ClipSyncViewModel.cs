using System.Windows;
using WinDeploy.App.Services.Clip;
using WinDeploy.App.Views.Clip;

namespace WinDeploy.App.ViewModels.Clip;

/// <summary>Host for the 「剪贴板同步」 page. A single <see cref="ClipSyncManager"/> drives all three tabs:
/// 设备与配对 (discover/pair/start-stop + log), 共享剪贴板 (the replicated board), and 设置. The manager's
/// change events are funnelled here and marshalled to the child VMs, so the children don't each subscribe to
/// background events. Inbound pairing requests are answered by showing the PIN dialog.</summary>
public sealed class ClipSyncViewModel : ObservableObject
{
    private readonly ClipSyncManager _manager = new();

    public ClipPeersViewModel Peers { get; }
    public ClipBoardViewModel Board { get; }
    public ClipSettingsViewModel Settings { get; }

    public ClipSyncViewModel()
    {
        Peers = new ClipPeersViewModel(_manager);
        Board = new ClipBoardViewModel(_manager);
        Settings = new ClipSettingsViewModel(_manager);
        _current = Peers;

        ShowPeersCommand = new RelayCommand(_ => Select(0));
        ShowBoardCommand = new RelayCommand(_ => Select(1));
        ShowSettingsCommand = new RelayCommand(_ => Select(2));

        // Funnel manager events to the right child on the UI thread.
        _manager.PeersChanged += () => OnUi(Peers.RefreshPeers);
        _manager.LinksChanged += () => OnUi(() => { Peers.RefreshLinks(); Board.RaiseAllPropertiesChanged(); });
        _manager.BoardChanged += () => OnUi(Board.Reload);
        _manager.Log += line => OnUi(() => Peers.AppendLog(line));

        // Inbound pairing → prompt for the PIN (with retry feedback) on the UI thread.
        _manager.PinPrompt = PromptPinAsync;
    }

    private int _tab;
    private object _current;
    public object Current { get => _current; private set => Set(ref _current, value); }

    public bool IsPeersTab => _tab == 0;
    public bool IsBoardTab => _tab == 1;
    public bool IsSettingsTab => _tab == 2;

    public RelayCommand ShowPeersCommand { get; }
    public RelayCommand ShowBoardCommand { get; }
    public RelayCommand ShowSettingsCommand { get; }

    private void Select(int t)
    {
        _tab = t;
        Current = t switch { 1 => Board, 2 => Settings, _ => (object)Peers };
        OnPropertyChanged(nameof(IsPeersTab));
        OnPropertyChanged(nameof(IsBoardTab));
        OnPropertyChanged(nameof(IsSettingsTab));
    }

    private Task<string?> PromptPinAsync(string peerName, int attempt)
    {
        var tcs = new TaskCompletionSource<string?>();
        var app = Application.Current;
        if (app == null) { tcs.SetResult(null); return tcs.Task; }
        app.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var dlg = new ClipPinDialog(peerName, attempt) { Owner = app.MainWindow };
                var ok = dlg.ShowDialog();
                tcs.SetResult(ok == true ? dlg.Pin : null);
            }
            catch { tcs.SetResult(null); }
        });
        return tcs.Task;
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    /// <summary>Page shown — keep the live status / "since" timers ticking.</summary>
    public void Activate() => Peers.StartLive();

    /// <summary>Page hidden — stop the timers (sharing keeps running in the background).</summary>
    public void Deactivate() => Peers.StopLive();

    /// <summary>App closing — stop discovery / listener / monitor and drop all links.</summary>
    public void Shutdown() => _manager.Dispose();
}
