using WinDeploy.Core.Config;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Pre-downloads the installers/packages for a selection into one folder, for air-gapped / USB
/// deployment (NFR-6 离线支持). Portable/exe assets are fetched directly; winget items use
/// <c>winget download</c> where the source supports it. Nothing is installed.</summary>
public static class OfflineKit
{
    public static async Task<List<ConfigResult>> DownloadAsync(IEnumerable<CatalogItem> items, string outDir, EngineContext ctx)
    {
        Directory.CreateDirectory(outDir);
        var results = new List<ConfigResult>();

        foreach (var it in items)
        {
            if (ctx.Ct.IsCancellationRequested) break;
            var ins = it.Install;
            try
            {
                switch (ins.Method)
                {
                    case "portable" or "exe" when !string.IsNullOrWhiteSpace(ins.Url) && ins.Url != "…":
                    {
                        var ext = GuessExt(ins.Url!);
                        var dest = Path.Combine(outDir, $"{it.Id}{ext}");
                        ctx.Step(Localizer.Format("engine.offline.downloading", it.Name));
                        await Download.ToFileAsync(ins.Url!, dest, ctx, ctx.Ct);
                        results.Add(ConfigResult.Ok(it.Name, Localizer.Format("engine.offline.downloaded", Path.GetFileName(dest))));
                        break;
                    }
                    case "winget" when !string.IsNullOrWhiteSpace(ins.Id):
                    {
                        ctx.Step($"winget download {ins.Id} …");
                        var pkgDir = Path.Combine(outDir, it.Id);
                        Directory.CreateDirectory(pkgDir);
                        var args = new List<string>
                        {
                            "download", "--id", ins.Id!, "-e", "-d", pkgDir,
                            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
                        };
                        if (ins.Source != null) { args.Add("--source"); args.Add(ins.Source); }
                        var r = await Proc.RunAsync("winget", args, ct: ctx.Ct);
                        results.Add(r.Ok
                            ? ConfigResult.Ok(it.Name, Localizer.Format("engine.offline.wingetDownloaded", it.Id))
                            : ConfigResult.Skip(it.Name, Localizer.T("engine.offline.wingetUnsupported")));
                        break;
                    }
                    default:
                        results.Add(ConfigResult.Skip(it.Name, Localizer.Format("engine.offline.cannotPredownload", ins.Method)));
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { results.Add(ConfigResult.Fail(it.Name, ex.Message)); }
        }
        return results;
    }

    private static string GuessExt(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.EndsWith(".7z")) return ".7z";
        if (lower.EndsWith(".zip")) return ".zip";
        if (lower.EndsWith(".msi")) return ".msi";
        var e = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrEmpty(e) ? ".exe" : e;
    }
}
