using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Removes an installed item. <paramref name="purgeData"/> also deletes its config/data dirs.</summary>
public static class Uninstaller
{
    public static async Task<StepOutcome> UninstallAsync(CatalogItem item, PathResolver pr, bool purgeData,
        CancellationToken ct = default, Action<string>? report = null)
    {
        var ins = item.Install;
        switch (ins.Method)
        {
            case "winget" when ins.Id != null:
                report?.Invoke(Localizer.Format("engine.uninstall.wingetUninstall", ins.Id));
                var r = await Proc.RunAsync("winget", new[]
                {
                    "uninstall", "--id", ins.Id, "-e", "--disable-interactivity", "--accept-source-agreements",
                }, ct: ct);
                if (!r.Ok) return StepOutcome.Fail(Localizer.Format("engine.uninstall.wingetExit", r.ExitCode));
                break;

            case "winget-bundle" when ins.Ids is { Count: > 0 } ids:
                foreach (var id in ids)
                {
                    report?.Invoke(Localizer.Format("engine.uninstall.wingetUninstall", id));
                    await Proc.RunAsync("winget", new[] { "uninstall", "--id", id, "-e", "--disable-interactivity" }, ct: ct);
                }
                break;

            case "portable" when ins.ExtractTo != null:
                report?.Invoke(Localizer.T("engine.uninstall.deleteDir"));
                if (!TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.ExtractTo)))
                    return StepOutcome.Fail(Localizer.T("engine.uninstall.deleteInstallDirFail"));
                break;

            case "git" when ins.Dest != null:
                report?.Invoke(Localizer.T("engine.uninstall.deleteCloneDir"));
                if (!TryDeleteDir(pr.Resolve(item.InstallPathOverride ?? ins.Dest)))
                    return StepOutcome.Fail(Localizer.T("engine.uninstall.deleteDirFail"));
                break;

            default:
                return StepOutcome.Fail(Localizer.T("engine.uninstall.unsupported"));
        }

        if (purgeData && item.Config?.Target is { } target)
        {
            report?.Invoke(Localizer.T("engine.uninstall.purgeData"));
            TryDeleteDir(pr.Resolve(target));
        }

        return StepOutcome.Done(purgeData ? Localizer.T("engine.uninstall.donePurged") : Localizer.T("engine.uninstall.done"));
    }

    private static bool TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); return true; }
        catch { return false; }
    }
}
