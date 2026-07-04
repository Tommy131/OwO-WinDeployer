using System.Windows;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.Services.Software;

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

        // Rich dialog: rendered release notes + pre-release warning + update / ignore / later.
        var updateDlg = new Views.Common.UpdateAvailableDialog(check) { Owner = owner };
        updateDlg.ShowDialog();
        switch (updateDlg.Choice)
        {
            case Views.Common.UpdateChoice.Ignore:
                var ig = SettingsStore.Load();
                ig.IgnoredUpdateVersion = check.Tag;
                SettingsStore.Save(ig);
                AuditLog.Action($"自更新：用户忽略版本 {check.Tag}");
                return;
            case Views.Common.UpdateChoice.Later:
                return;
        }

        AuditLog.Action($"自更新：用户确认更新到 {check.Tag}（{(check.IsPrerelease ? "预览版" : "正式版")}）");

        if (!SelfUpdateInstaller.IsInstallDirWritable())
        {
            Dialogs.Show(Localizer.T("update.auto.notWritable"), Localizer.T("settings.update.dialogTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(check.HtmlUrl);
            return;
        }

        // Fetch the exact release BY TAG (not "latest", which never returns a pre-release) so the right assets download.
        GhRelease? rel;
        try { rel = await GitHub.ReleaseByTagAsync(OwOWinDeployer.App.AppInfo.Repo, check.Tag, force: true); }
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
