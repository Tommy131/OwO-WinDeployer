using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.Services.Software;

/// <summary>Which of the three release.yml variants this running copy is — determines which release asset
/// to fetch. Detected purely from files already on disk, no persisted "how was I installed" flag needed.</summary>
public enum BuildVariant { SingleFile, WithRuntime, Framework }

/// <summary>Downloads and applies the app's own update in place. Matches the release asset to how THIS copy
/// was published, downloads + extracts it, then hands off to a detached PowerShell helper that waits for this
/// process to exit, OVERLAYS the new files onto the install directory (adds/overwrites only — never deletes),
/// and relaunches the app. The overlay-only rule is deliberate: the portable <c>data\</c> folder and any
/// personally-saved <c>catalog/profiles/*.json</c> aren't shipped in the release zip, so they're never touched;
/// only the temporary download/staging folder is cleaned up afterward.</summary>
public static class SelfUpdateInstaller
{
    public static string InstallDir => AppContext.BaseDirectory;

    /// <summary>Detected from files already on disk: self-contained single-file builds ship no OwOWinDeployer.dll;
    /// self-contained multi-file builds ship hostfxr.dll alongside it; framework-dependent builds don't.</summary>
    public static BuildVariant DetectVariant()
    {
        if (!File.Exists(Path.Combine(InstallDir, "OwOWinDeployer.dll"))) return BuildVariant.SingleFile;
        return File.Exists(Path.Combine(InstallDir, "hostfxr.dll")) ? BuildVariant.WithRuntime : BuildVariant.Framework;
    }

    /// <summary>Best-effort probe: can we write into the install directory? False under an admin-only
    /// location (e.g. Program Files) when not elevated — the caller then falls back to a manual download.</summary>
    public static bool IsInstallDirWritable()
    {
        try
        {
            var probe = Path.Combine(InstallDir, $".update-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string VariantSuffix(BuildVariant v) => v switch
    {
        BuildVariant.SingleFile => "singlefile",
        BuildVariant.WithRuntime => "with-runtime",
        _ => "framework",
    };

    /// <summary>All full-package zip assets on the release (excludes the bare bootstrap OwOWinDeployer.exe asset,
    /// which isn't a complete package to update in place from).</summary>
    public static List<GhAsset> ZipAssets(GhRelease rel)
        => rel.Assets.Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The asset matching this install's variant, or null if the release doesn't have one (e.g. the
    /// naming convention changed) — the caller should then let the user pick manually.</summary>
    public static GhAsset? MatchingAsset(GhRelease rel, BuildVariant variant)
    {
        var suffix = $"-win-x64-{VariantSuffix(variant)}.zip";
        return ZipAssets(rel).FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Download <paramref name="asset"/> and extract it to a fresh staging folder under
    /// %TEMP%\OwOWinDeployerUpdate\&lt;tag&gt;\extracted\. Throws (with a localized, user-facing message) on any
    /// failure — network error, corrupt zip, or an extracted package missing OwOWinDeployer.exe.</summary>
    public static async Task<string> DownloadAndExtractAsync(GhAsset asset, string tag,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "OwOWinDeployerUpdate", tag.TrimStart('v', 'V'));
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { /* stale leftover, best effort */ } }
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, asset.Name);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OwO-Win-Deployer-SelfUpdate");
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var f = File.Create(zipPath);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                await f.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                progress?.Report((read, total));
            }
        }

        var extractDir = Path.Combine(root, "extracted");
        Directory.CreateDirectory(extractDir);
        try { ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true); }
        catch (Exception ex) { throw new InvalidOperationException(Localizer.T("update.auto.badPackage"), ex); }
        finally { try { File.Delete(zipPath); } catch { /* best effort — the extracted copy is what matters */ } }

        // release.yml zips the staged folder itself (Compress-Archive -Path <dir>), so the archive holds one
        // top-level wrapper folder (e.g. "OwO-Win-Deployer-v1.2.6.1-win-x64-singlefile/") rather than the
        // payload at its root. Descend through single-subfolder levels (mirrors PortableInstaller's "strip")
        // until we reach the level that actually contains OwOWinDeployer.exe.
        var srcRoot = extractDir;
        while (!File.Exists(Path.Combine(srcRoot, "OwOWinDeployer.exe")))
        {
            var subs = Directory.GetDirectories(srcRoot);
            var files = Directory.GetFiles(srcRoot);
            if (subs.Length != 1 || files.Length != 0) break;
            srcRoot = subs[0];
        }

        if (!File.Exists(Path.Combine(srcRoot, "OwOWinDeployer.exe")))
            throw new InvalidOperationException(Localizer.T("update.auto.badPackage"));

        return srcRoot;
    }

    /// <summary>Hand off to a detached PowerShell helper: wait for this process to exit, overlay-copy the
    /// staged files (from <paramref name="stagingDir"/>, as returned by <see cref="DownloadAndExtractAsync"/>)
    /// onto the install dir (add/overwrite only — never deletes), delete the whole per-tag temp download
    /// folder, then relaunch the app. Fire-and-forget: the helper outlives this process. The caller must shut
    /// the app down immediately after calling this.</summary>
    public static void LaunchApplyAndExit(string stagingDir, string tag)
    {
        // Recompute the same fixed "<tmp>\OwOWinDeployerUpdate\<tag>\" root DownloadAndExtractAsync used, rather
        // than inferring it from stagingDir (which may sit one or more wrapper-folder levels deeper — see the
        // strip-descend above). The helper script is written as a SIBLING of that root (not inside it), so
        // deleting the root at the end can never touch the still-running script's own file.
        var tempRoot = Path.Combine(Path.GetTempPath(), "OwOWinDeployerUpdate");
        var tagRoot = Path.Combine(tempRoot, tag.TrimStart('v', 'V'));
        var script = Path.Combine(tempRoot, $"apply-update-{Guid.NewGuid():N}.ps1");
        var exePath = Path.Combine(InstallDir, "OwOWinDeployer.exe");
        File.WriteAllText(script, ApplyScript, System.Text.Encoding.UTF8);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-ParentPid", Environment.ProcessId.ToString(),
            "-Source", stagingDir, "-Dest", InstallDir, "-Exe", exePath, "-CleanupRoot", tagRoot,
        })
            psi.ArgumentList.Add(a);

        Process.Start(psi);   // detached — outlives this process once we shut down
    }

    // Waits for the current process to exit, then overlay-copies $Source's files onto $Dest (creating
    // subfolders as needed, retrying briefly per file in case AV/indexing holds a just-freed handle). Never
    // deletes anything under $Dest — only the source package's own files are copied in, so data\ and any
    // personal catalog/profiles/*.json survive untouched. Removes the whole per-tag download folder
    // ($CleanupRoot, a sibling of this script — never the script's own directory), relaunches, then the
    // script deletes itself last (its own temp .ps1 file is the only thing left behind on any failure).
    private const string ApplyScript = """
        param([int]$ParentPid, [string]$Source, [string]$Dest, [string]$Exe, [string]$CleanupRoot)
        try { Wait-Process -Id $ParentPid -Timeout 30 -ErrorAction SilentlyContinue } catch {}
        Start-Sleep -Milliseconds 500

        Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
          $rel = $_.FullName.Substring($Source.Length).TrimStart('\')
          $destPath = Join-Path $Dest $rel
          $destDir = Split-Path $destPath -Parent
          if ($destDir -and -not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
          for ($i = 0; $i -lt 6; $i++) {
            try { Copy-Item -LiteralPath $_.FullName -Destination $destPath -Force -ErrorAction Stop; break }
            catch { Start-Sleep -Milliseconds 500 }
          }
        }

        try { Remove-Item -LiteralPath $CleanupRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        try { Start-Process -FilePath $Exe -WorkingDirectory $Dest } catch {}
        try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch {}
        """;
}
