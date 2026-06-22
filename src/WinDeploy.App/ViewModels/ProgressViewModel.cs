using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core.Engine;

namespace WinDeploy.App.ViewModels;

/// <summary>The "运行进度" page. Operations are serialized by the host; each task is a row created via
/// <see cref="Enqueue"/> (shown 排队 while it waits), then driven by Start/Step/Live/Done against that
/// row reference — so a new task never overwrites the running one. History persists; tiles are cumulative.</summary>
public sealed class ProgressViewModel : ObservableObject
{
    private readonly Stopwatch _sw = new();
    private ProgressItemViewModel? _currentRow;
    private string _verb = "安装";
    private int _runTotal, _runDone, _runOk, _runFailed;

    public ObservableCollection<ProgressItemViewModel> Items { get; } = new();

    private double _percent;
    public double Percent { get => _percent; set => Set(ref _percent, value); }

    private string _overall = "准备中";
    public string Overall { get => _overall; set => Set(ref _overall, value); }

    private string _current = "";
    public string Current { get => _current; set => Set(ref _current, value); }

    public string CurrentLabel => $"正在{_verb}";

    private string _liveProgress = "";
    public string LiveProgress { get => _liveProgress; private set => Set(ref _liveProgress, value); }
    public void OnLiveProgress(string msg) => LiveProgress = msg;

    private string _eta = "";
    public string Eta { get => _eta; set => Set(ref _eta, value); }

    private string _log = "";
    public string Log { get => _log; set => Set(ref _log, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    public event Action? CancelRequested;
    public RelayCommand CancelCommand => _cancelCommand ??= new RelayCommand(_ => CancelRequested?.Invoke(), _ => IsRunning);
    private RelayCommand? _cancelCommand;

    public RelayCommand ClearHistoryCommand => _clearHistory ??= new RelayCommand(_ => ClearHistory(), _ => Items.Count > 0 && !IsRunning);
    private RelayCommand? _clearHistory;
    public RelayCommand OpenTotalLogCommand => _openTotal ??= new RelayCommand(_ => RunHistory.Open());
    private RelayCommand? _openTotal;
    public RelayCommand ClearTotalLogCommand => _clearTotal ??= new RelayCommand(_ => ClearTotalLog());
    private RelayCommand? _clearTotal;

    // Cumulative tiles over the full history (reset only by 清空历史).
    public int OkCount => Items.Count(i => i.Kind == "ok");
    public int FailedCount => Items.Count(i => i.Kind == "failed");
    public int RunningCount => Items.Count(i => i.Kind == "running");
    public int QueuedCount => Items.Count(i => i.Kind == "queued");

    /// <summary>Add a 排队 row immediately (before the op acquires the run lock). Returns the row.</summary>
    public ProgressItemViewModel Enqueue(string id, string name, string method)
    {
        var vm = new ProgressItemViewModel(name, method) { Id = id, Status = "排队", Kind = "queued" };
        Items.Add(vm);
        RaiseCounts();
        return vm;
    }

    public void BeginRun(string verb, int total)
    {
        _verb = verb;
        _runTotal = total; _runDone = 0; _runOk = 0; _runFailed = 0;
        IsRunning = true;
        Percent = 0; Current = ""; Eta = ""; LiveProgress = "";
        if (Items.Count > total) Append("");
        Append($"── {DateTime.Now:HH:mm:ss} {verb} ──");
        _sw.Restart();
        OnPropertyChanged(nameof(CurrentLabel));
        RaiseCounts();
    }

    public void Start(ProgressItemViewModel row)
    {
        _currentRow = row;
        row.Status = $"{_verb}中"; row.Kind = "running"; row.MarkStarted();
        Current = row.Name;
        LiveProgress = "";
        Append($"→ {_verb} {row.Name} …");
        RaiseCounts();
    }

    /// <summary>A granular step for the running row + the log.</summary>
    public void OnStep(string msg)
    {
        _currentRow?.AddDetail($"{DateTime.Now:HH:mm:ss}  {msg}");
        Append($"   · {msg}");
    }

    public void Done(ProgressItemViewModel row, StepStatus status, string? message)
    {
        (row.Status, row.Kind) = status switch
        {
            StepStatus.Ok => ("成功", "ok"),
            StepStatus.Failed => ("失败", "failed"),
            _ => ("已装（跳过）", "skip"),
        };
        row.MarkEnded();
        PersistRecord(row, status, message);

        if (message != "already installed")
        {
            _runDone++;
            if (status == StepStatus.Failed) { _runFailed++; Append($"✗ {row.Name}: {message}"); }
            else { _runOk++; Append(string.IsNullOrEmpty(message) ? $"✓ {row.Name}" : $"✓ {row.Name} — {message}"); }
            Percent = _runTotal == 0 ? 100 : Math.Round(_runDone * 100.0 / _runTotal);
            Eta = EstimateEta();
        }
        Overall = $"进度 {_runDone} / {_runTotal}";
        RaiseCounts();
    }

    public void EndRun()
    {
        IsRunning = false;
        _currentRow = null;
        Current = ""; Eta = ""; LiveProgress = "";
        Percent = 100;
        Overall = $"完成 · 成功 {_runOk} · 失败 {_runFailed}";
        Append("— 结束 —");
        RaiseCounts();
    }

    private void ClearHistory()
    {
        if (MessageBox.Show("确定清空运行进度历史记录？（屏幕显示，不影响独立总日志文件）", "清空历史",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Items.Clear();
        _currentRow = null;
        Log = "";
        Percent = 0; Overall = "准备中"; Current = ""; Eta = "";
        RaiseCounts();
    }

    private void ClearTotalLog()
    {
        if (MessageBox.Show("确定清空运行进度独立总日志（progress.jsonl）？此操作不可恢复。", "清空总日志",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        RunHistory.Clear();
    }

    private void PersistRecord(ProgressItemViewModel row, StepStatus status, string? message)
    {
        try
        {
            RunHistory.Append(new RunRecord
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Op = _verb,
                Id = row.Id,
                Name = row.Name,
                Status = status switch { StepStatus.Ok => "成功", StepStatus.Failed => "失败", _ => "跳过" },
                Message = message,
                StartedAt = row.StartTime?.ToString("HH:mm:ss") ?? "",
                EndedAt = row.EndTime?.ToString("HH:mm:ss") ?? "",
                DurationMs = row.StartTime != null && row.EndTime != null ? (long)(row.EndTime.Value - row.StartTime.Value).TotalMilliseconds : 0,
                Steps = row.Details.ToList(),
            });
        }
        catch { /* logging must not break the run */ }
    }

    private string EstimateEta()
    {
        if (_runDone == 0 || _runDone >= _runTotal) return "";
        var per = _sw.Elapsed.TotalSeconds / _runDone;
        var remain = TimeSpan.FromSeconds(per * (_runTotal - _runDone));
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
