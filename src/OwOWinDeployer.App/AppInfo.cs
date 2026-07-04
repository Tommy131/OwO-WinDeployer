// ============================================================================
//  OwO! Win Deployer — Community Edition
//  Copyright (C) 2026 HanskiJay (GitHub: Tommy131) <hanskijay@owoblog.com>
//  Blog: https://owoblog.com/  ·  Donation: https://buymeacoffee.com/hanskijay
//
//  This file is part of OwO! Win Deployer. The author's copyright and identity
//  below are the SINGLE SOURCE referenced across the app (window title, About
//  page, welcome dialog, audit log, assembly metadata). Do not strip or alter
//  this notice: the open-source Community Edition is licensed for personal
//  study and research only — commercial use requires the author's authorization.
// ============================================================================
using System.Reflection;
using System.Security.Principal;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App;

/// <summary>Central app identity and the author's authorship information — the one place the product name,
/// edition, version and developer contact live. It is referenced pervasively (title bar, About page, welcome
/// dialog, audit log), so these constants can't be quietly removed without breaking the build. Please keep the
/// authorship intact: this is the open-source <b>Community Edition</b>, for personal use only.</summary>
public static class AppInfo
{
    public const string Name = "OwO! Win Deployer";

    /// <summary>Open-source edition marker. The public/community build is "Community" — personal use only;
    /// a commercial edition requires authorization from the author.</summary>
    public const string Edition = "Community";

    // ── author / copyright (do not alter — see the file header) ──────────────
    /// <summary>Author display name.</summary>
    public const string Author = "HanskiJay";
    /// <summary>Author's GitHub handle.</summary>
    public const string AuthorGitHub = "Tommy131";
    public const string AuthorGitHubUrl = "https://github.com/Tommy131";
    public const string Email = "hanskijay@owoblog.com";
    public const string DonationUrl = "https://buymeacoffee.com/hanskijay";
    public const string ServiceUrl = "https://owoblog.com/service";
    public const string BlogUrl = "https://owoblog.com/";
    public const int Year = 2026;

    /// <summary>Full copyright line shown in the UI and embedded in assembly metadata.</summary>
    public const string Copyright = "© 2026 HanskiJay (Tommy131) · hanskijay@owoblog.com";

    /// <summary>License of the open-source edition.</summary>
    public const string License = "CC BY-NC-SA 4.0";

    /// <summary>owner/repo on GitHub — drives the self-update release check.</summary>
    public const string Repo = "Tommy131/owo-win-deployer";

    public static string Version
    {
        get
        {
            // Prefer the informational version — it carries the full SemVer from <Version>, INCLUDING any
            // pre-release suffix (e.g. "1.3.1-rc.1"), which the numeric AssemblyVersion strips. That suffix is
            // essential: the self-update check must order pre-releases correctly, and the UI must be able to tell
            // the user they're running a preview. Strip the "+<commit>" build metadata the SDK may append.
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "1.0.0";
            return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string TitleWithVersion => $"{Name} v{Version} · {Edition}";

    /// <summary>True when the process is running elevated (member of the Administrators role). Computed once.</summary>
    public static bool IsAdministrator { get; } = ComputeElevated();

    private static bool ComputeElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Window title; appends a localized "(Administrator)" only when elevated, to distinguish admin runs.</summary>
    public static string TitleWithRole => IsAdministrator ? $"{TitleWithVersion}{Localizer.T("app.adminSuffix")}" : TitleWithVersion;
}
