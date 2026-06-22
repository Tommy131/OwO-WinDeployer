using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>One process row under a software group.</summary>
public sealed class ProcRowViewModel
{
    public int Pid { get; }
    public string Name { get; }
    public string MemText { get; }

    public ProcRowViewModel(int pid, string name, long memBytes)
    {
        Pid = pid;
        Name = name;
        MemText = memBytes > 0 ? $"{memBytes / 1024.0 / 1024:0.0} MB" : "—";
    }
}

/// <summary>A catalog item and its running processes.</summary>
public sealed class AppProcGroupViewModel
{
    public CatalogItem Model { get; }
    public string Name => Model.Name;
    public ObservableCollection<ProcRowViewModel> Processes { get; } = new();
    public string CountText => $"{Processes.Count} 个进程";

    public AppProcGroupViewModel(CatalogItem model) => Model = model;
}

/// <summary>The "进程管理" page: a task-manager scoped to the catalog. Lists running processes per
/// software, ends a single process inline, and routes 结束全部 / 重启 through the run-progress page.</summary>
public sealed class ProcessManagerViewModel : ObservableObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());

    public ObservableCollection<AppProcGroupViewModel> Groups { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand EndProcessCommand { get; }
    public RelayCommand EndAllCommand { get; }
    public RelayCommand RestartCommand { get; }

    /// <summary>Raised for group-level 结束全部 / 重启 — (item, "stop" | "restart").</summary>
    public event Action<CatalogItem, string>? OperationRequested;

    public ProcessManagerViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        EndProcessCommand = new RelayCommand(p => { if (p is ProcRowViewModel r) EndOne(r); });
        EndAllCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) OperationRequested?.Invoke(g.Model, "stop"); });
        RestartCommand = new RelayCommand(p => { if (p is AppProcGroupViewModel g) OperationRequested?.Invoke(g.Model, "restart"); });
    }

    public void Initialize(Catalog catalog, PathResolver resolver)
    {
        _catalog = catalog;
        _resolver = resolver;
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsReady)); }
    }
    public bool IsReady => !_isBusy;

    private string _summary = "点击刷新以扫描软件进程";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public bool IsEmpty => Groups.Count == 0;

    public async Task RefreshAsync()
    {
        if (_catalog == null || _isBusy) return;
        IsBusy = true;
        Summary = "正在扫描进程 …";

        var items = _catalog.Items.ToList();
        var resolver = _resolver;
        var found = await Task.Run(() =>
        {
            var list = new List<(CatalogItem Item, List<ProcItem> Procs)>();
            foreach (var it in items)
            {
                List<ProcItem> ps;
                try { ps = ProcessControl.Find(it, resolver); } catch { ps = new(); }
                if (ps.Count > 0) list.Add((it, ps));
            }
            return list;
        });

        Groups.Clear();
        foreach (var (item, procs) in found)
        {
            var g = new AppProcGroupViewModel(item);
            foreach (var p in procs) g.Processes.Add(new ProcRowViewModel(p.Pid, p.Name, p.MemBytes));
            Groups.Add(g);
        }

        Summary = found.Count == 0
            ? "未发现软件列表中的软件正在运行"
            : $"{found.Count} 个软件运行中 · 共 {found.Sum(f => f.Procs.Count)} 个进程";
        OnPropertyChanged(nameof(IsEmpty));
        IsBusy = false;
    }

    private void EndOne(ProcRowViewModel r)
    {
        if (MessageBox.Show($"确定结束进程 {r.Name}（PID {r.Pid}）？", "结束进程",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ok = ProcessControl.Kill(r.Pid);
        AuditLog.Action($"结束进程 {r.Name} (PID {r.Pid})：{(ok ? "成功" : "失败")}");
        _ = RefreshAsync();
    }
}
