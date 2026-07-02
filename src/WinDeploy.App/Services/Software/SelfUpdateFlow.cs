using System.Windows;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Services.Software;

/// <summary>End-to-end self-update UX, shared by the startup check and the Settings「检查更新」button: confirm
/// → detect this install's variant → match (or let the user pick) the release asset → download with a
/// progress window → hand off to the detached apply script → shut the app down so it relaunches as the new
/// version. Falls back to opening the release page in the browser whenever a step isn't possible (install dir
/// not writable, no matching asset, network failure) — the user is never left without a path forward.</summary>
public static class SelfUpdateFlow
{
    public static async Task OfferAsync(UpdateCheck check)
    {
        var owner = Application.Current.MainWindow;
        var msg = Localizer.Format("update.auto.confirm", WinDeploy.App.AppInfo.Name, check.Latest, check.Current);
        if (Dialogs.Show(msg, Localizer.T("settings.update.dialogTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information)
            != MessageBoxResult.Yes)
            return;

        AuditLog.Action($"自更新：用户确认更新到 v{check.Latest}");

        if (!SelfUpdateInstaller.IsInstallDirWritable())
        {
            Dialogs.Show(Localizer.T("update.auto.notWritable"), Localizer.T("settings.update.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(check.HtmlUrl);
            return;
        }

        GhRelease? rel;
        try { rel = await GitHub.LatestReleaseAsync(WinDeploy.App.AppInfo.Repo, force: true); }
        catch { rel = null; }
        if (rel == null)
        {
            Dialogs.Show(Localizer.T("update.auto.relFail"), Localizer.T("settings.update.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(check.HtmlUrl);
            return;
        }

        var variant = SelfUpdateInstaller.DetectVariant();
        var asset = SelfUpdateInstaller.MatchingAsset(rel, variant);
        if (asset == null)
        {
            // Auto-match failed (e.g. asset naming changed) — let the user pick from whatever actually shipped.
            var zips = SelfUpdateInstaller.ZipAssets(rel);
            if (zips.Count == 0)
            {
                Dialogs.Show(Localizer.T("update.auto.noAssets"), Localizer.T("settings.update.dialogTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                OpenUrl(check.HtmlUrl);
                return;
            }
            var labels = zips.Select(a => $"{a.Name}  ({Mb(a.Size)})").ToList();
            var dlg = new Views.Common.ChoiceDialog(Localizer.T("update.pick.title"), Localizer.T("update.pick.body"), labels, 0)
                { Owner = owner };
            if (dlg.ShowDialog() != true) return;
            asset = zips[dlg.SelectedIndex];
        }

        var cts = new CancellationTokenSource();
        var progressDlg = new Views.Common.UpdateProgressDialog(Localizer.T("update.progress.title"), cts) { Owner = owner };
        var downloadTask = SelfUpdateInstaller.DownloadAndExtractAsync(asset, rel.Tag,
            new Progress<(long, long)>(p => progressDlg.Report(p.Item1, p.Item2)), cts.Token);

        progressDlg.Show();
        string staged;
        try { staged = await downloadTask; }
        catch (OperationCanceledException) { AuditLog.Action("自更新：用户取消下载"); return; }
        catch (Exception ex)
        {
            Dialogs.Show(Localizer.Format("update.auto.downloadFail", ex.Message), Localizer.T("settings.update.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(check.HtmlUrl);
            return;
        }
        finally { try { progressDlg.Close(); } catch { /* already closing */ } }

        AuditLog.Action($"自更新：v{check.Latest} 下载完成，准备安装并重启");
        Dialogs.Show(Localizer.T("update.auto.readyRestart"), Localizer.T("settings.update.dialogTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);

        try { SelfUpdateInstaller.LaunchApplyAndExit(staged, rel.Tag); }
        catch (Exception ex)
        {
            Dialogs.Show(Localizer.Format("update.auto.applyFail", ex.Message), Localizer.T("settings.update.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(check.HtmlUrl);
            return;
        }
        Application.Current.Shutdown();
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static string Mb(long b) => b >= 1024L * 1024 * 1024 ? $"{b / 1024.0 / 1024 / 1024:0.0} GB" : $"{b / 1024.0 / 1024:0.0} MB";
}
