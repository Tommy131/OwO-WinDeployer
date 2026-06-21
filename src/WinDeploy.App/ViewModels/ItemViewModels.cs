using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.Core.Models;

namespace WinDeploy.App.ViewModels;

public sealed class NavItemViewModel
{
    public string Glyph { get; }
    public string Label { get; }
    public object Page { get; }

    public NavItemViewModel(string glyph, string label, object page)
    {
        Glyph = glyph;
        Label = label;
        Page = page;
    }
}

public sealed class PlaceholderViewModel
{
    public string Title { get; }
    public string Message { get; }

    public PlaceholderViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }
}

/// <summary>One software card on the install center.</summary>
public sealed class AppItemViewModel : ObservableObject
{
    private static readonly Dictionary<string, (string Bg, string Fg)> Palette = new()
    {
        ["dev"] = ("#FAECE7", "#712B13"),
        ["system"] = ("#F1EFE8", "#444441"),
        ["ide"] = ("#E6F1FB", "#0C447C"),
        ["ai"] = ("#EEEDFE", "#3C3489"),
        ["office"] = ("#EAF3DE", "#27500A"),
        ["media"] = ("#FBEAF0", "#72243E"),
        ["db-api"] = ("#E1F5EE", "#085041"),
        ["vm"] = ("#FAEEDA", "#633806"),
        ["games"] = ("#F1EFE8", "#444441"),
    };

    public CatalogItem Model { get; }

    public AppItemViewModel(CatalogItem model)
    {
        Model = model;
        _isSelected = model.Default;
        var (bg, fg) = Palette.TryGetValue(model.Category, out var c) ? c : ("#F1EFE8", "#444441");
        ChipBackground = (Brush)new BrushConverter().ConvertFromString(bg)!;
        ChipForeground = (Brush)new BrushConverter().ConvertFromString(fg)!;
    }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Summary => Model.Summary ?? "";
    public string Method => Model.Install.Method;
    public string Badge => Model.Name.Length > 0 ? Model.Name[..1].ToUpperInvariant() : "?";
    public Brush ChipBackground { get; }
    public Brush ChipForeground { get; }

    /// <summary>Brand icon thumbnail (assets/icons/&lt;id&gt;.png); null falls back to the letter badge.</summary>
    public ImageSource? IconImage { get; private set; }
    public bool HasIcon => IconImage != null;
    public bool ShowLetter => IconImage == null;

    public void LoadIcon(string repoRoot)
    {
        try
        {
            var path = Path.Combine(repoRoot, "assets", "icons", Model.Id + ".png");
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;          // don't lock the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            IconImage = bmp;
            OnPropertyChanged(nameof(IconImage));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(ShowLetter));
        }
        catch { /* keep the letter fallback */ }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) SelectionChanged?.Invoke(); }
    }

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    private bool? _installed;
    public bool? Installed
    {
        get => _installed;
        set
        {
            if (Set(ref _installed, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsInstalled));
            }
        }
    }

    public bool IsInstalled => _installed == true;
    public string StatusText => _installed switch { null => "检测中…", true => "已装", _ => "未装" };

    public event Action? SelectionChanged;
}

public sealed class CategoryGroupViewModel : ObservableObject
{
    public string Key { get; }
    public string Title { get; }
    public System.Collections.ObjectModel.ObservableCollection<AppItemViewModel> Items { get; } = new();

    public CategoryGroupViewModel(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string CountText => $"{SelectedCount} / {Items.Count} 已选";

    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    public void RaiseCount()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One row in the running-progress list.</summary>
public sealed class ProgressItemViewModel : ObservableObject
{
    public string Name { get; }
    public string Method { get; }

    public ProgressItemViewModel(string name, string method)
    {
        Name = name;
        Method = method;
    }

    private string _status = "排队";
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>queued | running | ok | failed | skip — drives the row pill colour via DataTrigger.</summary>
    private string _kind = "queued";
    public string Kind { get => _kind; set => Set(ref _kind, value); }
}
