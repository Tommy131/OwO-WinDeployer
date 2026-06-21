using System.Collections.ObjectModel;
using System.Diagnostics;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.ViewModels;

/// <summary>The "运行进度" page: overall bar + ETA, current task, per-item rows, live log.</summary>
public sealed class ProgressViewModel : ObservableObject
{
    private readonly Dictionary<string, ProgressItemViewModel> _byId = new();
    private readonly Stopwatch _sw = new();
    private int _total;
    private int _completed;

    public ObservableCollection<ProgressItemViewModel> Items { get; } = new();

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    private string _overall = "准备中";
    public string Overall { get => _overall; set => Set(ref _overall, value); }

    private string _current = "";
    public string Current { get => _current; set => Set(ref _current, value); }

    private string _eta = "";
    public string Eta { get => _eta; set => Set(ref _eta, value); }

    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    public int OkCount => Items.Count(i => i.Kind == "ok");
    public int FailedCount => Items.Count(i => i.Kind == "failed");
    public int RunningCount => Items.Count(i => i.Kind == "running");
    public int QueuedCount => Items.Count(i => i.Kind == "queued");

    public void Begin(IReadOnlyList<PlanItem> plan)
    {
        Items.Clear();
        _byId.Clear();
        foreach (var pi in plan)
        {
            var installed = pi.Status == PlanStatus.Installed;
            var vm = new ProgressItemViewModel(pi.Item.Name, pi.Item.Install.Method)
            {
                Status = installed ? "已装（跳过）" : "排队",
                Kind = installed ? "skip" : "queued",
            };
            Items.Add(vm);
            _byId[pi.Item.Id] = vm;
        }
        _total = plan.Count(p => p.Status == PlanStatus.ToInstall);
        _completed = 0;
        IsRunning = true;
        Percent = 0;
        Current = "";
        Overall = $"待装 {_total} 项";
        Log = "";
        _sw.Restart();
        RaiseCounts();
    }

    public void OnStart(PlanItem pi)
    {
        Current = pi.Item.Name;
        if (_byId.TryGetValue(pi.Item.Id, out var vm)) { vm.Status = "安装中"; vm.Kind = "running"; }
        Append($"→ 安装 {pi.Item.Name} …");
        RaiseCounts();
    }

    public void OnDone(RunResult r)
    {
        if (_byId.TryGetValue(r.Item.Id, out var vm))
        {
            (vm.Status, vm.Kind) = r.Status switch
            {
                StepStatus.Ok => ("成功", "ok"),
                StepStatus.Failed => ("失败", "failed"),
                _ => (vm.Status, vm.Kind),
            };
        }

        if (r.Message != "already installed")
        {
            _completed++;
            Percent = _total == 0 ? 100 : Math.Round(_completed * 100.0 / _total);
            Eta = EstimateEta();
            if (r.Status == StepStatus.Failed) Append($"✗ {r.Item.Name}: {r.Message}");
            else Append($"✓ {r.Item.Name}");
        }
        Overall = $"进度 {_completed} / {_total}";
        RaiseCounts();
    }

    public void Complete()
    {
        IsRunning = false;
        Current = "";
        Eta = "";
        Percent = 100;
        Overall = $"完成 · 成功 {OkCount} · 失败 {FailedCount}";
        Append("— 全部结束 —");
    }

    private string EstimateEta()
    {
        if (_completed == 0 || _completed >= _total) return "";
        var per = _sw.Elapsed.TotalSeconds / _completed;
        var remain = TimeSpan.FromSeconds(per * (_total - _completed));
        return remain.TotalMinutes >= 1
            ? $"约剩 {(int)remain.TotalMinutes} 分 {remain.Seconds} 秒"
            : $"约剩 {remain.Seconds} 秒";
    }

    private void Append(string line) => Log += line + Environment.NewLine;

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(OkCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(RunningCount));
        OnPropertyChanged(nameof(QueuedCount));
    }
}
