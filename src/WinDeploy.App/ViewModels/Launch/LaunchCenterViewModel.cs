using System.Collections.ObjectModel;
using System.Windows;
using WinDeploy.App.Services.Infra;
using WinDeploy.App.Services.Launch;
using WinDeploy.App.Views.Common;
using WinDeploy.App.Views.Launch;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.ViewModels.Launch;

/// <summary>The 快速启动 page: a user-curated to-do list of launchable targets (apps / files / shortcuts /
/// websites / commands). Each row can be checked off, reordered, edited or opened. Fully self-contained —
/// loads and saves its own launcher.json, no engine/catalog dependency.</summary>
public sealed class LaunchCenterViewModel : LocalizedObject
{
    private readonly LaunchConfig _config;

    public ObservableCollection<LaunchItemViewModel> Items { get; } = new();

    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ClearDoneCommand { get; }
    public RelayCommand OpenWidgetCommand { get; }

    /// <summary>Raised when the user clicks 打开桌面组件; the owner (MainViewModel) turns the widget setting on.</summary>
    public event Action? OpenWidgetRequested;

    private bool _isWidgetOpen;
    /// <summary>Whether the desktop widget is currently shown (kept in sync by MainViewModel). Drives the button's
    /// enabled state + label.</summary>
    public bool IsWidgetOpen
    {
        get => _isWidgetOpen;
        set
        {
            if (!Set(ref _isWidgetOpen, value)) return;
            OnPropertyChanged(nameof(OpenWidgetLabel));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public string OpenWidgetLabel => Localizer.T(_isWidgetOpen ? "launcher.widget.opened" : "launcher.widget.open");

    /// <summary>No items yet → the view shows an empty-state hint instead of the list.</summary>
    public bool IsEmpty => Items.Count == 0;

    public LaunchCenterViewModel()
    {
        _config = LaunchStore.Load();

        // Enforce the "no duplicates" invariant on load — drop any repeats left over from before the rule
        // existed (keep the first occurrence), then persist the cleaned list.
        var seen = new HashSet<string>();
        var deduped = _config.Items.Where(m => seen.Add(m.DupKey())).ToList();
        if (deduped.Count != _config.Items.Count) { _config.Items = deduped; LaunchStore.Save(_config); }

        foreach (var m in _config.Items) Items.Add(Wrap(m));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        AddCommand = new RelayCommand(_ => Add());
        EditCommand = new RelayCommand(p => { if (p is LaunchItemViewModel r) Edit(r); });
        DeleteCommand = new RelayCommand(p => { if (p is LaunchItemViewModel r) Delete(r); });
        LaunchCommand = new RelayCommand(p => { if (p is LaunchItemViewModel r) Open(r); });
        MoveUpCommand = new RelayCommand(p => { if (p is LaunchItemViewModel r) Move(r, -1); });
        MoveDownCommand = new RelayCommand(p => { if (p is LaunchItemViewModel r) Move(r, +1); });
        ClearDoneCommand = new RelayCommand(_ => ClearDone());
        OpenWidgetCommand = new RelayCommand(_ => OpenWidgetRequested?.Invoke(), _ => !_isWidgetOpen);
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        foreach (var r in Items) r.Refresh();
    }

    private LaunchItemViewModel Wrap(LaunchItem m)
    {
        var vm = new LaunchItemViewModel(m);
        vm.Changed += Save;   // persist the Done checkbox toggle
        return vm;
    }

    private void Add()
    {
        var dlg = new LaunchEditDialog(null) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result is not { } item) return;
        if (IsDuplicate(item)) { WarnDuplicate(item); return; }
        _config.Items.Add(item);
        Items.Add(Wrap(item));
        Save();
        AuditLog.Action($"快速启动：新增「{item.Title}」→ {item.Target}");
    }

    private void Edit(LaunchItemViewModel row)
    {
        var dlg = new LaunchEditDialog(row.Model) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result is not { } edited) return;
        if (IsDuplicate(edited, exceptId: row.Model.Id)) { WarnDuplicate(edited); return; }
        row.Model.CopyEditableFrom(edited);   // commit the working copy onto the live model
        row.Refresh();
        Save();
        AuditLog.Action($"快速启动：编辑「{row.Model.Title}」");
    }

    /// <summary>True when another item already launches the same thing (same kind + target + args).</summary>
    private bool IsDuplicate(LaunchItem candidate, string? exceptId = null)
    {
        var key = candidate.DupKey();
        return Items.Any(r => r.Model.Id != exceptId && r.Model.DupKey() == key);
    }

    private static void WarnDuplicate(LaunchItem item)
        => Dialogs.Show(Localizer.Format("launcher.dup.body", string.IsNullOrWhiteSpace(item.Title) ? item.Target : item.Title),
            Localizer.T("launcher.dup.title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Delete(LaunchItemViewModel row)
    {
        if (Dialogs.Show(Localizer.Format("launcher.delete.confirm", row.Title),
                Localizer.T("launcher.delete.title"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        _config.Items.Remove(row.Model);
        Items.Remove(row);
        Save();
        AuditLog.Action($"快速启动：删除「{row.Title}」");
    }

    private void Open(LaunchItemViewModel row)
    {
        var (ok, detail) = LaunchRunner.Run(row.Model);
        if (ok)
        {
            AuditLog.Action($"快速启动：打开「{row.Title}」→ {row.Model.Target}");
            ToastService.TryShow(Localizer.T("launcher.title"), Localizer.Format("launcher.launch.ok", row.Title));
        }
        else
        {
            AuditLog.Action($"快速启动：打开「{row.Title}」失败 — {detail}");
            Dialogs.Show(Localizer.Format("launcher.launch.fail", row.Title, detail),
                Localizer.T("launcher.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Move(LaunchItemViewModel row, int delta)
    {
        var i = Items.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        _config.Items.Clear();
        _config.Items.AddRange(Items.Select(r => r.Model));
        Save();
    }

    private void ClearDone()
    {
        var done = Items.Where(r => r.Done).ToList();
        if (done.Count == 0) return;
        if (Dialogs.Show(Localizer.Format("launcher.clearDone.confirm", done.Count),
                Localizer.T("launcher.clearDone.title"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        foreach (var r in done) { _config.Items.Remove(r.Model); Items.Remove(r); }
        Save();
        AuditLog.Action($"快速启动：清除 {done.Count} 个已完成项");
    }

    private void Save() => LaunchStore.Save(_config);
}
