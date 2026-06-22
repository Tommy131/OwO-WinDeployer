using System.Text;
using System.Windows;
using WinDeploy.App.Services;
using WinDeploy.Core;
using WinDeploy.Core.Config;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Export;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>The "高级工具" page (开发人员模式): GUI front-ends for the professional Core features — 环境体检
/// (doctor), catalog 校验, 版本锁定 (lock.json), winget DSC 导出, 离线部署包, 迁移工具包. Dev-only.</summary>
public sealed class AdvancedToolsViewModel : ObservableObject
{
    private Catalog? _catalog;
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private string _repoRoot = "";
    private string _catalogDir = "";

    public RelayCommand DoctorCommand { get; }
    public RelayCommand ValidateCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand DscCommand { get; }
    public RelayCommand OfflineCommand { get; }
    public RelayCommand MigrateExportCommand { get; }
    public RelayCommand MigrateImportCommand { get; }
    public RelayCommand EditHostsCommand { get; }
    public RelayCommand ClearCommand { get; }

    public AdvancedToolsViewModel()
    {
        DoctorCommand = new RelayCommand(_ => _ = RunDoctorAsync(), _ => !IsBusy);
        ValidateCommand = new RelayCommand(_ => RunValidate(), _ => !IsBusy);
        LockCommand = new RelayCommand(_ => _ = RunLockAsync(), _ => !IsBusy);
        DscCommand = new RelayCommand(_ => RunDsc(), _ => !IsBusy);
        OfflineCommand = new RelayCommand(_ => _ = RunOfflineAsync(), _ => !IsBusy);
        MigrateExportCommand = new RelayCommand(_ => _ = RunMigrateExportAsync(), _ => !IsBusy);
        MigrateImportCommand = new RelayCommand(_ => RunMigrateImport(), _ => !IsBusy);
        EditHostsCommand = new RelayCommand(_ => EditHosts());
        ClearCommand = new RelayCommand(_ => Output = "");
    }

    public void Initialize(Catalog catalog, PathResolver resolver, string repoRoot, string catalogDir)
    {
        _catalog = catalog;
        _resolver = resolver;
        _repoRoot = repoRoot;
        _catalogDir = catalogDir;
    }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private string _output = "选择上方工具开始。所有操作的结果会显示在这里。";
    public string Output { get => _output; set => Set(ref _output, value); }

    private void Append(string line) => Output += (Output.Length == 0 ? "" : "\n") + line;
    private void Section(string title) => Append((Output.Length == 0 ? "" : "\n") + $"── {title} ──");

    // ③ doctor
    private async Task RunDoctorAsync()
    {
        if (_catalog == null) return;
        IsBusy = true;
        Section("环境体检");
        var findings = await Doctor.RunAsync(_catalog, _resolver);
        foreach (var f in findings)
        {
            var tag = f.Level switch { HealthLevel.Error => "✗", HealthLevel.Warn => "!", _ => "✓" };
            Append($"{tag} {f.Title} — {f.Detail.Replace("\n", "；")}");
            if (f.Fix != null) Append($"    → {f.Fix}");
        }
        Append($"完成 · 错误 {findings.Count(f => f.Level == HealthLevel.Error)} · 警告 {findings.Count(f => f.Level == HealthLevel.Warn)}");
        IsBusy = false;
    }

    // ④ validate
    private void RunValidate()
    {
        if (_catalog == null) return;
        Section("catalog 校验");
        var issues = CatalogValidator.Validate(_catalog, _repoRoot);
        if (issues.Count == 0) Append("✓ 校验通过，无问题");
        foreach (var i in issues.OrderBy(i => i.Level))
            Append($"{(i.Level == IssueLevel.Error ? "✗" : "!")} [{i.ItemId}] {i.Message}");
        Append($"错误 {issues.Count(i => i.Level == IssueLevel.Error)} · 警告 {issues.Count(i => i.Level == IssueLevel.Warn)}");
    }

    // ① lock
    private async Task RunLockAsync()
    {
        if (_catalog == null) return;
        IsBusy = true;
        Section("生成版本锁定 lock.json");
        Append("正在采集已装版本（winget export）…");
        var lf = await Lockfile.CaptureAsync(_catalog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        lf.Save(_catalogDir);
        AuditLog.Action($"生成 lock.json：{lf.Versions.Count} 个版本");
        Append($"✓ 已写入 {Lockfile.DefaultPath(_catalogDir)} · 钉定 {lf.Versions.Count} 个版本");
        Append("提交 lock.json 后，其它机器可用「apply --locked」复刻相同版本。");
        IsBusy = false;
    }

    // ⑥ DSC export
    private void RunDsc()
    {
        if (_catalog == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "导出 winget configure (DSC)", FileName = "windeploy.dsc.yaml", Filter = "YAML (*.yaml)|*.yaml" };
        if (dlg.ShowDialog() != true) return;
        var items = Selection.Resolve(_catalog, null, null, all: true, null);
        System.IO.File.WriteAllText(dlg.FileName, DscExport.Build(items));
        Section("导出 winget configure (DSC)");
        Append($"✓ 已导出 → {dlg.FileName}");
        Append($"在目标机运行：winget configure -f \"{dlg.FileName}\"");
    }

    // ⑩ offline kit
    private async Task RunOfflineAsync()
    {
        if (_catalog == null) return;
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = "选择离线包输出目录" };
        if (fd.ShowDialog() != true) return;
        IsBusy = true;
        Section("离线 / U 盘部署包");
        var items = Selection.Resolve(_catalog, null, null, false, null);   // 默认预选项
        Append($"预下载 {items.Count} 个默认项 → {fd.FolderName}");
        var ctx = new EngineContext
        {
            Path = _resolver, RepoRoot = _repoRoot, Ct = System.Threading.CancellationToken.None,
            Report = msg => Application.Current.Dispatcher.Invoke(() => Append("  " + msg)),
        };
        var results = await OfflineKit.DownloadAsync(items, fd.FolderName, ctx);
        foreach (var r in results)
            Append($"{(r.Status == StepStatus.Failed ? "✗" : r.Status == StepStatus.Skipped ? "·" : "✓")} {r.Name} — {r.Message}");
        Append($"完成 · 文件位于 {fd.FolderName}");
        IsBusy = false;
    }

    // ⑯ migration kit export
    private async Task RunMigrateExportAsync()
    {
        if (_catalog == null) return;
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = "选择迁移工具包输出目录" };
        if (fd.ShowDialog() != true) return;
        IsBusy = true;
        Section("导出迁移工具包");
        var ctx = new EngineContext { Path = _resolver, RepoRoot = _repoRoot, Ct = System.Threading.CancellationToken.None };
        var results = await MigrationKit.ExportAsync(_catalog, ctx, fd.FolderName, it => Detection.IsInstalledAsync(it, _resolver));
        foreach (var r in results) Append($"{(r.Status == StepStatus.Ok ? "✓" : "·")} {r.Name} — {r.Message}");
        AuditLog.Action($"导出迁移工具包 → {fd.FolderName}");
        Append($"✓ 工具包已生成：{fd.FolderName}（含 configs/、manifest.json、RESTORE.txt）");
        IsBusy = false;
    }

    // ⑯ migration kit import
    private void RunMigrateImport()
    {
        var fd = new Microsoft.Win32.OpenFolderDialog { Title = "选择要还原的迁移工具包目录" };
        if (fd.ShowDialog() != true) return;
        Section("从迁移工具包还原");
        var (results, manifest) = MigrationKit.Import(fd.FolderName, _repoRoot);
        foreach (var r in results) Append($"{(r.Status == StepStatus.Ok ? "✓" : "·")} {r.Name} — {r.Message}");
        if (manifest is { InstalledIds.Count: > 0 })
            Append($"建议还原软件（软件安装中心勾选或 CLI）：apply --only {string.Join(",", manifest.InstalledIds)}");
        AuditLog.Action($"还原迁移工具包 ← {fd.FolderName}");
    }

    // ⑪ hosts.json editor
    private void EditHosts()
    {
        try
        {
            var path = HostProfiles.FilePath(_catalogDir);
            if (!System.IO.File.Exists(path))
            {
                var example = System.IO.Path.Combine(_catalogDir, "hosts.example.json");
                if (System.IO.File.Exists(example)) System.IO.File.Copy(example, path);
                else System.IO.File.WriteAllText(path, "{\n  \"" + Environment.MachineName + "\": \"dev\",\n  \"*\": \"dev\"\n}\n");
                Section("主机名 → 预设 (hosts.json)");
                Append($"✓ 已创建 {path}");
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Append("打开 hosts.json 失败：" + ex.Message); }
    }
}
