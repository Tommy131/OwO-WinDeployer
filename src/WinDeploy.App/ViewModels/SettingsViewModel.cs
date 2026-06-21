using WinDeploy.App.Services;

namespace WinDeploy.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;

    public SettingsViewModel()
    {
        _s = SettingsStore.Load();
        _devRoot = _s.DevRoot ?? "%USERPROFILE%/dev";
        _toolsDir = _s.ToolsDir ?? "%LOCALAPPDATA%/tools";
        _repoUrl = _s.RepoUrl ?? "https://github.com/Tommy131/win-provision.git";
        _mirror = _s.Mirror ?? "";
        _redactKeywords = _s.RedactKeywords ?? "";
        SettingsPath = SettingsStore.FilePath;
        SaveCommand = new RelayCommand(_ => Save());
    }

    private string _devRoot;
    public string DevRoot { get => _devRoot; set { if (Set(ref _devRoot, value)) Note = ""; } }

    private string _toolsDir;
    public string ToolsDir { get => _toolsDir; set { if (Set(ref _toolsDir, value)) Note = ""; } }

    private string _repoUrl;
    public string RepoUrl { get => _repoUrl; set { if (Set(ref _repoUrl, value)) Note = ""; } }

    private string _mirror;
    public string Mirror { get => _mirror; set { if (Set(ref _mirror, value)) Note = ""; } }

    private string _redactKeywords;
    public string RedactKeywords { get => _redactKeywords; set { if (Set(ref _redactKeywords, value)) Note = ""; } }

    public string SettingsPath { get; }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public RelayCommand SaveCommand { get; }
    public event Action? Saved;

    private void Save()
    {
        _s.DevRoot = DevRoot.Trim();
        _s.ToolsDir = ToolsDir.Trim();
        _s.RepoUrl = RepoUrl.Trim();
        _s.Mirror = Mirror.Trim();
        _s.RedactKeywords = RedactKeywords.Trim();
        SettingsStore.Save(_s);
        Note = "已保存。脱敏关键词即时生效；路径变量在下次启动生效。";
        Saved?.Invoke();
    }

    public static string[] ParseKeywords(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ' ', '\n', '\r', '\t', ';', '，', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
