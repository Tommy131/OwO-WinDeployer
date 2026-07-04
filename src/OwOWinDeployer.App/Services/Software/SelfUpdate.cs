using OwOWinDeployer.Core.I18n;
using OwOWinDeployer.Core.Util;

namespace OwOWinDeployer.App.Services.Software;

/// <summary>Result of an app self-update check. <see cref="Tag"/> is the raw release tag (e.g. "v1.3.1-rc.1") used
/// to fetch the exact release and to remember an ignored version; <see cref="Notes"/> is the release body
/// (Markdown) for display; <see cref="IsPrerelease"/> marks a preview build so the UI can warn.</summary>
public sealed record UpdateCheck(
    bool Available, string Latest, string Current, string Tag, string HtmlUrl,
    string ReleaseName, string Notes, bool IsPrerelease, string? Error);

/// <summary>Checks GitHub for a newer release of the app itself (<see cref="AppInfo.Repo"/>). Shared by the
/// startup check and the Settings「检查更新」button so the version logic lives in one place. Pre-releases are
/// considered when the user opted in (设置) or when the running build is itself a pre-release.</summary>
public static class SelfUpdate
{
    public static async Task<UpdateCheck> CheckAsync(bool force = false, bool? includePrerelease = null)
    {
        var cur = AppInfo.Version;
        var fallbackUrl = $"https://github.com/{AppInfo.Repo}/releases/latest";
        try
        {
            bool includePre = includePrerelease
                ?? (SettingsStore.Load().PreReleaseUpdates || SemVer.IsPrerelease(cur));
            var rel = await GitHub.NewestAsync(AppInfo.Repo, includePre, force);
            if (rel == null)
                return new(false, "", cur, "", fallbackUrl, "", "", false, Localizer.T("update.noReleaseOrNet"));

            var latest = rel.Tag.TrimStart('v', 'V');
            var url = string.IsNullOrWhiteSpace(rel.HtmlUrl) ? fallbackUrl : rel.HtmlUrl;
            var available = !string.IsNullOrWhiteSpace(latest) && SemVer.Compare(latest, cur) > 0;
            return new(available, latest, cur, rel.Tag, url, rel.Name ?? "", rel.Body ?? "", rel.Prerelease, null);
        }
        catch (Exception ex) { return new(false, "", cur, "", fallbackUrl, "", "", false, ex.Message); }
    }
}
