using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using WinDeploy.App.Services;
using WinDeploy.Core.Util;

namespace WinDeploy.App.ViewModels;

/// <summary>Software detail page. Metadata comes from the Windows ARP registry first (installed
/// apps), then `winget show` fills gaps (version/publisher/homepage, incl. Store apps), and the
/// installed size is computed from the install folder when the registry omits it.</summary>
public sealed class DetailViewModel : ObservableObject
{
    public AppItemViewModel Item { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand OpenHomepageCommand { get; }

    private string? _installLocation;

    public DetailViewModel(AppItemViewModel item, Action back)
    {
        Item = item;
        BackCommand = new RelayCommand(_ => back());
        OpenHomepageCommand = new RelayCommand(_ => OpenHomepage());

        var ins = item.Model.Install;
        Source = ins.Method switch
        {
            "winget" => "winget",
            "winget-bundle" => "winget（合集）",
            "portable" => "便携包（zip）",
            "git" => "Git 仓库",
            "conda" => "conda 环境",
            "vscode-ext" => "VS Code 扩展",
            "script" => "脚本",
            _ => ins.Method,
        };
        PackageId = ins.Id ?? (ins.Ids is { Count: > 0 } ? string.Join(", ", ins.Ids) : "—");
        Load();
    }

    public ImageSource? IconImage => Item.IconImage;
    public bool HasIcon => Item.HasIcon;
    public bool ShowLetter => Item.ShowLetter;
    public string Badge => Item.Badge;
    public Brush ChipBackground => Item.ChipBackground;
    public Brush ChipForeground => Item.ChipForeground;
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public bool IsInstalled => Item.IsInstalled;
    public string StatusText => Item.IsInstalled ? "已安装" : "未安装";

    public string Source { get; }
    public string PackageId { get; }

    private string _version = "—";
    public string Version { get => _version; private set => Set(ref _version, value); }

    private string _size = "—";
    public string Size { get => _size; private set => Set(ref _size, value); }

    private string _installDate = "—";
    public string InstallDate { get => _installDate; private set => Set(ref _installDate, value); }

    private string _publisher = "—";
    public string Publisher { get => _publisher; private set => Set(ref _publisher, value); }

    private string _homepage = "—";
    public string Homepage
    {
        get => _homepage;
        private set { if (Set(ref _homepage, value)) OnPropertyChanged(nameof(HasHomepage)); }
    }
    public bool HasHomepage => _homepage.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        var e = Arp.Find(Item.Model.Detect?.Arp, Item.Name, IdToName(Item.Model.Install.Id));
        if (e != null)
        {
            if (!string.IsNullOrWhiteSpace(e.DisplayVersion)) Version = e.DisplayVersion!;
            if (e.EstimatedSizeKb > 0) Size = FormatKb(e.EstimatedSizeKb);
            if (!string.IsNullOrWhiteSpace(e.InstallDate)) InstallDate = FormatDate(e.InstallDate!);
            if (!string.IsNullOrWhiteSpace(e.Publisher)) Publisher = e.Publisher!;
            if (!string.IsNullOrWhiteSpace(e.Homepage)) Homepage = e.Homepage!;
            _installLocation = e.InstallLocation;
        }
        if (Version == "—" && Item.Model.Version != null) Version = Item.Model.Version;
        if (Homepage == "—" && !string.IsNullOrWhiteSpace(Item.Model.Homepage)) Homepage = Item.Model.Homepage!;
        if (Homepage == "—" && Homepages.TryGetValue(Item.Model.Id, out var hp)) Homepage = hp;

        _ = EnrichAsync();
    }

    private async Task EnrichAsync()
    {
        var id = Item.Model.Install.Id;
        if (!string.IsNullOrEmpty(id) && (Version == "—" || Publisher == "—" || Homepage == "—"))
        {
            try
            {
                var r = await Proc.RunAsync("winget", new[] { "show", "--id", id, "-e", "--disable-interactivity", "--accept-source-agreements" });
                if (r.Ok) ParseWingetShow(r.StdOut);
            }
            catch { /* ignore */ }
        }

        if (Size == "—" && !string.IsNullOrWhiteSpace(_installLocation) && Directory.Exists(_installLocation))
        {
            try
            {
                var bytes = await Task.Run(() => DirSize(_installLocation!));
                if (bytes > 0) Size = FormatBytes(bytes);
            }
            catch { /* ignore */ }
        }
    }

    private void ParseWingetShow(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var i = line.IndexOfAny(new[] { ':', '：' });
            if (i <= 0) continue;
            var label = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (value.Length == 0) continue;

            if (Version == "—" && Is(label, "Version", "版本")) Version = value;
            else if (Publisher == "—" && Is(label, "Publisher", "发布者")) Publisher = value;
            else if (Homepage == "—" && Is(label, "Homepage", "主页") && value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Homepage = value;
        }
    }

    private static bool Is(string label, params string[] names)
        => names.Any(n => label.Equals(n, StringComparison.OrdinalIgnoreCase));

    private void OpenHomepage()
    {
        if (!HasHomepage) return;
        try { Process.Start(new ProcessStartInfo(_homepage) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static long DirSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* skip */ }
            }
        }
        catch { /* skip */ }
        return total;
    }

    private static string FormatKb(long kb)
    {
        if (kb <= 0) return "—";
        double mb = kb / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static string FormatBytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024);
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    private static string FormatDate(string raw)
        => raw.Length == 8 && long.TryParse(raw, out _)
            ? $"{raw[..4]}-{raw.Substring(4, 2)}-{raw.Substring(6, 2)}"
            : raw;

    /// <summary>Derive a display-name from a winget id (Microsoft.VisualStudioCode → Visual Studio Code).</summary>
    private static string? IdToName(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var last = id.Split('.').Last();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < last.Length; i++)
        {
            if (i > 0 && char.IsUpper(last[i]) && !char.IsUpper(last[i - 1])) sb.Append(' ');
            sb.Append(last[i]);
        }
        return sb.ToString();
    }

    /// <summary>Official homepage per item — reliable, instant fallback when ARP/winget lack a URL.</summary>
    private static readonly Dictionary<string, string> Homepages = new()
    {
        ["git"] = "https://git-scm.com",
        ["gh"] = "https://cli.github.com",
        ["nodejs"] = "https://nodejs.org",
        ["python"] = "https://www.python.org",
        ["miniconda"] = "https://docs.conda.io/projects/miniconda",
        ["jdk17"] = "https://www.oracle.com/java/",
        ["go"] = "https://go.dev",
        ["dotnet-sdk"] = "https://dotnet.microsoft.com",
        ["cmake"] = "https://cmake.org",
        ["ffmpeg"] = "https://ffmpeg.org",
        ["pandoc"] = "https://pandoc.org",
        ["mingw"] = "https://winlibs.com",
        ["flutter"] = "https://flutter.dev",
        ["vcredist"] = "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist",
        ["windows-terminal"] = "https://aka.ms/terminal",
        ["huorong"] = "https://www.huorong.cn",
        ["vscode"] = "https://code.visualstudio.com",
        ["vscode-ext"] = "https://marketplace.visualstudio.com/vscode",
        ["vs2026"] = "https://visualstudio.microsoft.com",
        ["android-studio"] = "https://developer.android.com/studio",
        ["arduino"] = "https://www.arduino.cc/en/software",
        ["unity-hub"] = "https://unity.com",
        ["sublime-merge"] = "https://www.sublimemerge.com",
        ["comfyui"] = "https://www.comfy.org",
        ["lmstudio"] = "https://lmstudio.ai",
        ["claude"] = "https://claude.ai",
        ["windsurf"] = "https://windsurf.com",
        ["wechat"] = "https://weixin.qq.com",
        ["wecom"] = "https://work.weixin.qq.com",
        ["feishu"] = "https://www.feishu.cn",
        ["tencent-meeting"] = "https://meeting.tencent.com",
        ["obs"] = "https://obsproject.com",
        ["vlc"] = "https://www.videolan.org",
        ["irfanview"] = "https://www.irfanview.com",
        ["netease-music"] = "https://music.163.com",
        ["dbgate"] = "https://dbgate.org",
        ["apifox"] = "https://apifox.com",
        ["winscp"] = "https://winscp.net",
        ["vmware"] = "https://www.vmware.com/products/workstation-pro.html",
        ["steam"] = "https://store.steampowered.com/about/",
        ["epic"] = "https://store.epicgames.com",
    };
}
