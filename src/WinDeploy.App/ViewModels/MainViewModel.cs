using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly InstallEngine _engine = new();
    private PathResolver _resolver = new(new Dictionary<string, string>());
    private string _repoRoot = "";
    private Catalog? _catalog;

    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();
    public InstallCenterViewModel Install { get; } = new();
    public ProgressViewModel Progress { get; } = new();
    public ConfigSyncViewModel ConfigSync { get; } = new();
    public ExportViewModel Export { get; } = new();

    public MainViewModel()
    {
        Install.StartRequested += OnStartRequested;
        Load();

        NavItems.Add(new NavItemViewModel("", "软件安装中心", Install));
        NavItems.Add(new NavItemViewModel("", "配置同步", ConfigSync));
        NavItems.Add(new NavItemViewModel("", "运行进度", Progress));
        NavItems.Add(new NavItemViewModel("", "导出", Export));
        NavItems.Add(new NavItemViewModel("", "设置", new PlaceholderViewModel("设置", "路径变量向导、仓库地址、镜像源、脱敏清单")));
        SelectedNav = NavItems[0];
    }

    private NavItemViewModel? _selectedNav;
    public NavItemViewModel? SelectedNav
    {
        get => _selectedNav;
        set { if (Set(ref _selectedNav, value) && value != null) Current = value.Page; }
    }

    private object? _current;
    public object? Current { get => _current; set => Set(ref _current, value); }

    private void Load()
    {
        var dir = CatalogLoader.FindCatalogDir(AppContext.BaseDirectory)
                  ?? CatalogLoader.FindCatalogDir(Environment.CurrentDirectory);
        if (dir == null) { Install.LoadError = "找不到 catalog/catalog.json"; return; }

        var path = Path.Combine(dir, "catalog.json");
        _repoRoot = Path.GetDirectoryName(dir)!;
        try { _catalog = CatalogLoader.Load(path); }
        catch (Exception ex) { Install.LoadError = ex.Message; return; }

        _resolver = new PathResolver(_catalog.PathVars);
        Install.Initialize(_catalog, dir);
        ConfigSync.Initialize(_catalog, _resolver, _repoRoot);
        Export.Initialize(_catalog, _resolver, _repoRoot);
        _ = DetectAllAsync();
    }

    private async Task DetectAllAsync()
    {
        foreach (var group in Install.Groups)
            foreach (var item in group.Items)
                item.Installed = await Detection.IsInstalledAsync(item.Model, _resolver);
    }

    private void OnStartRequested()
    {
        if (_catalog == null) return;
        var selected = Install.Groups.SelectMany(g => g.Items)
            .Where(i => i.IsSelected).Select(i => i.Model).ToList();
        if (selected.Count == 0) return;

        SelectedNav = NavItems.First(n => ReferenceEquals(n.Page, Progress));
        _ = RunAsync(selected);
    }

    private async Task RunAsync(List<CatalogItem> selected)
    {
        var dispatcher = Application.Current.Dispatcher;
        var plan = await _engine.BuildPlanAsync(selected, _resolver);
        Progress.Begin(plan);

        var ctx = new EngineContext { Path = _resolver, RepoRoot = _repoRoot, Ct = CancellationToken.None };
        await _engine.ApplyAsync(plan, ctx, dryRun: false,
            onStart: pi => dispatcher.Invoke(() => Progress.OnStart(pi)),
            onDone: r => dispatcher.Invoke(() => Progress.OnDone(r)));

        dispatcher.Invoke(() => Progress.Complete());
    }
}
