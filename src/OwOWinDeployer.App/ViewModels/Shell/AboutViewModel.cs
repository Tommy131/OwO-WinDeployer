// ============================================================================
//  OwO! Win Deployer — Community Edition
//  Copyright (C) 2026 HanskiJay (GitHub: Tommy131) <hanskijay@owoblog.com>
//  Personal study/research use only — commercial use requires authorization.
// ============================================================================
using System.Windows.Media;
using OwOWinDeployer.App.Services;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.ViewModels.Shell;

/// <summary>One third-party dependency shown on the About page: what it is, its version, homepage and license.
/// The description is a localization key resolved in XAML via <c>S.about.dep.*</c>.</summary>
public sealed record DependencyInfo(string Name, string Version, string Url, string License, string DescKey)
{
    /// <summary>Localized one-line description (resolved from <see cref="DescKey"/> in the current language).</summary>
    public string Desc => Localizer.T(DescKey);
}

/// <summary>Backs the About page: app identity + edition, the author's authorship/contact (pulled from
/// <see cref="AppInfo"/>, the single source), the third-party dependency credits, the update check, and the
/// non-commercial disclaimer. Moved here out of Settings so "关于/关于开发者" live on their own page.</summary>
public sealed class AboutViewModel : ObservableObject
{
    // ── identity (all from AppInfo — the single, build-enforced source) ──────
    public string AppName => AppInfo.Name;
    public string AppTitle => AppInfo.TitleWithVersion;
    public string Version => AppInfo.Version;
    public string Edition => AppInfo.Edition;
    public string Copyright => AppInfo.Copyright;
    public string License => AppInfo.License;

    public string AuthorName => AppInfo.Author;
    public string AuthorGitHub => AppInfo.AuthorGitHub;
    public string GitHubUrl => AppInfo.AuthorGitHubUrl;
    public string Email => AppInfo.Email;
    public string MailUrl => $"mailto:{AppInfo.Email}";
    public string DonationUrl => AppInfo.DonationUrl;
    public string ServiceUrl => AppInfo.ServiceUrl;
    public string BlogUrl => AppInfo.BlogUrl;
    public ImageSource? AuthorAvatar => IconResolver.FromCatalogId("author");

    public string SettingsPath { get; } = SettingsStore.FilePath;

    /// <summary>True when the running build is a pre-release — the page flags it so the user knows it's a preview.</summary>
    public bool IsRunningPrerelease => OwOWinDeployer.Core.Util.SemVer.IsPrerelease(AppInfo.Version);

    /// <summary>Third-party libraries the app ships with, credited on the About page.</summary>
    public IReadOnlyList<DependencyInfo> Dependencies { get; } = new List<DependencyInfo>
    {
        new(".NET 10 · WPF", "10.0", "https://dotnet.microsoft.com/", "MIT", "about.dep.dotnet"),
        new("NAudio", "2.2.1", "https://github.com/naudio/NAudio", "MIT", "about.dep.naudio"),
        new("AvalonEdit", "6.3.0.90", "https://github.com/icsharpcode/AvalonEdit", "MIT", "about.dep.avalonedit"),
        new("LibreHardwareMonitorLib", "0.9.4", "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor", "MPL-2.0 / MIT", "about.dep.lhm"),
        new("System.Speech", "9.0.0", "https://www.nuget.org/packages/System.Speech", "MIT", "about.dep.speech"),
    };

    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public AboutViewModel()
    {
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckUpdateAsync());
        OpenLinkCommand = new RelayCommand(p => OpenUrl(p as string));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        _preReleaseUpdates = SettingsStore.Load().PreReleaseUpdates;
    }

    private string _updateNote = "";
    public string UpdateNote { get => _updateNote; set => Set(ref _updateNote, value); }

    private bool _preReleaseUpdates;
    /// <summary>接收预览版（pre-release）更新。开启后检查会把预览版也纳入；当前若运行的就是预览版则始终纳入。即时持久化。</summary>
    public bool PreReleaseUpdates
    {
        get => _preReleaseUpdates;
        set
        {
            if (!Set(ref _preReleaseUpdates, value)) return;
            var s = SettingsStore.Load();
            s.PreReleaseUpdates = value;
            SettingsStore.Save(s);
            AuditLog.Action($"接收预览版更新：{(value ? "开启" : "关闭")}");
        }
    }

    private async Task CheckUpdateAsync()
    {
        UpdateNote = Localizer.T("settings.update.checking");
        var r = await SelfUpdate.CheckAsync(force: true);
        if (r.Error != null) { UpdateNote = r.Error; return; }
        if (r.Available)
        {
            UpdateNote = Localizer.Format("settings.update.found", r.Latest);
            AuditLog.Action($"检查更新：发现新版本 v{r.Latest}（当前 v{r.Current}）");
            await SelfUpdateFlow.OfferAsync(r);
        }
        else UpdateNote = Localizer.Format("settings.update.upToDate", r.Current);
    }

    private void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsStore.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsStore.Folder) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
