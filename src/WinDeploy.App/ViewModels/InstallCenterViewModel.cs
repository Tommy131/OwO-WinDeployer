using System.Collections.ObjectModel;
using System.IO;
using WinDeploy.Core;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

/// <summary>The "软件安装中心" page: grouped, icon-forward, per-item selectable software cards.</summary>
public sealed class InstallCenterViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["dev"] = "开发工具链", ["system"] = "系统依赖", ["ide"] = "IDE / 编辑器",
        ["ai"] = "AI 工具", ["office"] = "办公 / 通讯", ["media"] = "媒体",
        ["db-api"] = "数据库 / API", ["vm"] = "虚拟化", ["games"] = "游戏平台",
    };

    private Catalog? _catalog;
    private string _catalogDir = "";

    public ObservableCollection<CategoryGroupViewModel> Groups { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();
    public RelayCommand StartCommand { get; }

    /// <summary>Raised when the user clicks 开始安装.</summary>
    public event Action? StartRequested;

    public InstallCenterViewModel()
        => StartCommand = new RelayCommand(_ => StartRequested?.Invoke(), _ => SelectedCount > 0);

    public void Initialize(Catalog catalog, string catalogDir)
    {
        _catalog = catalog;
        _catalogDir = catalogDir;

        Groups.Clear();
        foreach (var grp in catalog.Items.GroupBy(i => i.Category))
        {
            var g = new CategoryGroupViewModel(grp.Key, CategoryNames.GetValueOrDefault(grp.Key, grp.Key));
            foreach (var item in grp)
            {
                var vm = new AppItemViewModel(item);
                vm.SelectionChanged += () => OnSelectionChanged(g);
                g.Items.Add(vm);
            }
            Groups.Add(g);
        }

        Profiles.Clear();
        var pdir = Path.Combine(catalogDir, "profiles");
        if (Directory.Exists(pdir))
            foreach (var f in Directory.GetFiles(pdir, "*.json"))
                Profiles.Add(Path.GetFileNameWithoutExtension(f));

        RefreshCounts();
    }

    public int SelectedCount => Groups.Sum(g => g.Items.Count(i => i.IsSelected));
    public string StartLabel => $"开始安装 ({SelectedCount})";
    public string Subtitle => $"勾选要部署到本机的软件 · 已选 {SelectedCount} 项";

    private string? _loadError;
    public string? LoadError
    {
        get => _loadError;
        set { if (Set(ref _loadError, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_loadError);

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    private string? _selectedProfile;
    public string? SelectedProfile
    {
        get => _selectedProfile;
        set { if (Set(ref _selectedProfile, value) && value != null) ApplyProfile(value); }
    }

    private void OnSelectionChanged(CategoryGroupViewModel group)
    {
        group.RaiseCount();
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(Subtitle));
    }

    private void ApplyFilter()
    {
        var q = _searchText.Trim();
        foreach (var g in Groups)
        {
            var any = false;
            foreach (var i in g.Items)
            {
                i.IsVisible = q.Length == 0
                    || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || i.Summary.Contains(q, StringComparison.OrdinalIgnoreCase);
                any |= i.IsVisible;
            }
            g.IsVisible = any;
        }
    }

    private void ApplyProfile(string name)
    {
        if (_catalog == null) return;
        try
        {
            var profile = CatalogLoader.LoadProfile(_catalogDir, name);
            var ids = Selection.Resolve(_catalog, profile, null, false, null)
                .Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Groups)
            {
                foreach (var i in g.Items) i.IsSelected = ids.Contains(i.Id);
                g.RaiseCount();
            }
            RefreshCounts();
        }
        catch { /* ignore bad profile */ }
    }
}
