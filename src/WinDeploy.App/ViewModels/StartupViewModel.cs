using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

public sealed class StartupRowViewModel : ObservableObject
{
    public StartupEntry Entry { get; }

    public StartupRowViewModel(StartupEntry e)
    {
        Entry = e;
        Badge = e.Name.Length > 0 ? e.Name[..1].ToUpperInvariant() : "?";
        // Prefer the bundled icon cache (matched by name/exe to a catalog item); else the exe's real icon.
        try { IconImage = IconResolver.Resolve(e.Name, e.ExePath); }
        catch { /* letter fallback */ }
    }

    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string Source => Entry.Source;
    public bool NeedsAdmin => Entry.NeedsAdmin;

    public string Badge { get; }
    public ImageSource? IconImage { get; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;

    public bool Enabled => Entry.Enabled;
    public bool Disabled => !Entry.Enabled;
    public string ToggleLabel => Entry.Enabled ? "禁用" : "启用";

    public void RaiseState()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Disabled));
        OnPropertyChanged(nameof(ToggleLabel));
    }
}

/// <summary>The "启动项" page: manage Windows startup entries (Run keys + Startup folders) — enable /
/// disable (via StartupApproved, like Task Manager), remove, and reveal in Explorer.</summary>
public sealed class StartupViewModel : ObservableObject
{
    public ObservableCollection<StartupRowViewModel> Items { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenLocationCommand { get; }

    public StartupViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) Toggle(r); });
        RemoveCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) Remove(r); });
        OpenLocationCommand = new RelayCommand(p => { if (p is StartupRowViewModel r) OpenLocation(r); });
    }

    private string _summary = "点击刷新以扫描启动项";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public void Refresh()
    {
        Items.Clear();
        foreach (var e in StartupService.List().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Items.Add(new StartupRowViewModel(e));
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var on = Items.Count(i => i.Enabled);
        Summary = $"共 {Items.Count} 项 · 已启用 {on} · 已禁用 {Items.Count - on}";
    }

    private void Toggle(StartupRowViewModel r)
    {
        var (ok, msg) = StartupService.SetEnabled(r.Entry, !r.Entry.Enabled);
        if (!ok)
        {
            MessageBox.Show($"操作失败：{msg}\n\n（HKLM / 公共启动项需以管理员身份运行 WinDeploy）",
                "启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        r.RaiseState();
        AuditLog.Action($"启动项{(r.Entry.Enabled ? "启用" : "禁用")}：{r.Name}（{r.Source}）");
        UpdateSummary();
    }

    private void Remove(StartupRowViewModel r)
    {
        if (MessageBox.Show($"确定删除启动项「{r.Name}」？\n来源：{r.Source}\n\n仅从系统启动项中移除，不会卸载程序本身。",
                "删除启动项", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var (ok, msg) = StartupService.Remove(r.Entry);
        if (!ok)
        {
            MessageBox.Show($"删除失败：{msg}\n\n（HKLM / 公共启动项需以管理员身份运行）",
                "启动项", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AuditLog.Action($"启动项删除：{r.Name}（{r.Source}）");
        Items.Remove(r);
        UpdateSummary();
    }

    private void OpenLocation(StartupRowViewModel r)
    {
        try
        {
            var target = r.Entry.ExePath is { } exe && File.Exists(exe) ? exe : r.Entry.FilePath;
            if (!string.IsNullOrWhiteSpace(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }
}
