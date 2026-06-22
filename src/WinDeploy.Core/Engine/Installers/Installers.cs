using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine.Installers;

/// <summary>Routes winget output: progress tokens ("X MB / Y MB", "NN%") → live overwrite line;
/// meaningful text lines (Found / Downloading / Successfully…) → appended steps (deduped).</summary>
internal static class WingetProgress
{
    private static readonly Regex Pair = new(@"([\d.]+)\s*(B|KB|MB|GB)\s*/\s*([\d.]+)\s*(B|KB|MB|GB)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Pct = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>Handle one token; returns the new "last step" string for dedup.</summary>
    public static string? Handle(string tok, EngineContext ctx, Stopwatch sw, string? last)
    {
        var t = tok.Trim();
        if (t.Length == 0) return last;
        if (TryProgress(t, sw, out var live)) { ctx.Live(live); return last; }
        if (!t.Any(char.IsLetter)) return last;     // bar / spinner noise
        if (t == last) return last;
        ctx.Step(t);
        return t;
    }

    private static bool TryProgress(string t, Stopwatch sw, out string live)
    {
        live = "";
        var m = Pair.Match(t);
        if (m.Success)
        {
            var cur = Bytes(m.Groups[1].Value, m.Groups[2].Value);
            var tot = Bytes(m.Groups[3].Value, m.Groups[4].Value);
            if (tot > 0)
            {
                var secs = sw.Elapsed.TotalSeconds;
                var rate = secs > 0.1 ? cur / secs : 0;
                var eta = rate > 0 ? (tot - cur) / rate : 0;
                live = $"下载 {Mb(cur)} / {Mb(tot)} · {cur * 100.0 / tot:0}%{(rate > 0 ? " · " + Mb(rate) + "/s" : "")} · 约剩 {Eta(eta)}";
                return true;
            }
        }
        var pm = Pct.Match(t);
        if (pm.Success && !t.Any(char.IsLetter)) { live = $"进度 {pm.Groups[1].Value}%"; return true; }
        return false;
    }

    private static double Bytes(string val, string unit)
    {
        if (!double.TryParse(val, out var v)) return 0;
        return unit.ToUpperInvariant() switch { "GB" => v * 1024 * 1024 * 1024, "MB" => v * 1024 * 1024, "KB" => v * 1024, _ => v };
    }
    private static string Mb(double b) => b >= 1024.0 * 1024 * 1024 ? $"{b / 1024 / 1024 / 1024:0.0} GB" : $"{b / 1024 / 1024:0.0} MB";
    private static string Eta(double s) => s >= 60 ? $"{(int)(s / 60)}分{(int)(s % 60)}秒" : $"{s:0}秒";
}

public sealed class WingetInstaller : IInstaller
{
    public string Method => "winget";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Id is null) return StepOutcome.Fail("winget id missing");
        var args = new List<string>
        {
            "install", "--id", ins.Id, "-e",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
        };
        if (ins.Scope != null) { args.Add("--scope"); args.Add(ins.Scope); }
        if (ins.Source != null) { args.Add("--source"); args.Add(ins.Source); }
        if (item.Version != null) { args.Add("--version"); args.Add(item.Version); }
        if (item.InstallPathOverride != null) { args.Add("--location"); args.Add(ctx.Path.Resolve(item.InstallPathOverride)); }
        ctx.Step($"winget 安装 {ins.Id}{(item.Version != null ? " " + item.Version : "")} …");
        var sw = Stopwatch.StartNew();
        string? last = null;
        var r = await Proc.RunStreamingAsync("winget", args, tok => last = WingetProgress.Handle(tok, ctx, sw, last), ct: ctx.Ct);
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"winget exit {r.ExitCode}");
    }
}

public sealed class WingetBundleInstaller : IInstaller
{
    public string Method => "winget-bundle";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ids = item.Install.Ids;
        if (ids is null || ids.Count == 0) return StepOutcome.Fail("winget-bundle ids missing");
        var failed = new List<string>();
        foreach (var id in ids)
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "install", "--id", id, "-e",
                "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
            }, ct: ctx.Ct);
            if (!r.Ok) failed.Add(id);
        }
        return failed.Count == 0 ? StepOutcome.Done() : StepOutcome.Fail("failed: " + string.Join(", ", failed));
    }
}

public sealed class PortableInstaller : IInstaller
{
    public string Method => "portable";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Url is null || ins.ExtractTo is null) return StepOutcome.Fail("portable needs url + extractTo");

        var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.ExtractTo);
        var dlDir = ctx.DownloadDir ?? System.IO.Path.GetTempPath();
        System.IO.Directory.CreateDirectory(dlDir);
        var lower = ins.Url.ToLowerInvariant();
        var isArchive = lower.EndsWith(".7z") || lower.EndsWith(".zip");
        var ext = lower.EndsWith(".7z") ? ".7z" : lower.EndsWith(".zip") ? ".zip"
                : System.IO.Path.GetExtension(lower) is { Length: > 0 } e ? e : ".bin";
        var tmpZip = System.IO.Path.Combine(dlDir, $"windeploy_{item.Id}{ext}");
        var tmpEx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"windeploy_{item.Id}_x");

        ctx.Step($"开始下载 {ins.Url} …");
        await Download.ToFileAsync(ins.Url, tmpZip, ctx, ctx.Ct);

        if (ins.Sha256 is { Length: > 0 } sha && sha != "…")
        {
            ctx.Step("校验 SHA256 …");
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tmpZip, ctx.Ct)));
            if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
                return StepOutcome.Fail($"sha256 mismatch ({actual[..12]}…)");
        }
        else Log.Warn($"{item.Id}: no sha256 set — skipping integrity check");

        Directory.CreateDirectory(dest);
        if (!isArchive)
        {
            // A standalone portable file (e.g. a single .exe) — place it directly in the install dir.
            var fileName = System.IO.Path.GetFileName(new Uri(ins.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = item.Id + ext;
            ctx.Step($"写入安装目录 {dest} …");
            File.Copy(tmpZip, System.IO.Path.Combine(dest, fileName), overwrite: true);
        }
        else
        {
            if (Directory.Exists(tmpEx)) Directory.Delete(tmpEx, true);
            if (ext == ".7z")
            {
                var seven = Find7z();
                if (seven == null) return StepOutcome.Fail("解压 .7z 需要 7-Zip，请先安装 7-Zip 后重试");
                ctx.Step("用 7-Zip 解压 …");
                var ex = await Proc.RunAsync(seven, new[] { "x", "-o" + tmpEx, tmpZip, "-y" }, ct: ctx.Ct);
                if (!ex.Ok) return StepOutcome.Fail($"7-Zip 解压失败 退出码 {ex.ExitCode}");
            }
            else
            {
                ctx.Step("解压 …");
                ZipFile.ExtractToDirectory(tmpZip, tmpEx);
            }

            var srcRoot = tmpEx;
            for (var i = 0; i < (ins.Strip ?? 0); i++)
            {
                var subs = Directory.GetDirectories(srcRoot);
                var files = Directory.GetFiles(srcRoot);
                if (subs.Length == 1 && files.Length == 0) srcRoot = subs[0];
                else break;
            }

            ctx.Step($"写入安装目录 {dest} …");
            CopyDir(srcRoot, dest);
        }

        foreach (var p in ins.Path ?? new List<string>())
            EnvPath.AddToUserPath(ctx.Path.Resolve(p));

        try { File.Delete(tmpZip); Directory.Delete(tmpEx, true); } catch { /* best effort */ }
        return StepOutcome.Done();
    }

    internal static string? Find7z()
    {
        var onPath = CommandFinder.Find("7z");
        if (onPath != null) return onPath;
        foreach (var p in new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dst));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dst), overwrite: true);
    }
}

public sealed class GitInstaller : IInstaller
{
    public string Method => "git";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Repo is null || ins.Dest is null) return StepOutcome.Fail("git needs repo + dest");
        var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.Dest);

        ProcResult r;
        if (Directory.Exists(System.IO.Path.Combine(dest, ".git")))
        {
            ctx.Step($"git 拉取更新 {dest} …");
            r = await Proc.RunAsync("git", new[] { "-C", dest, "pull", "--ff-only" }, ct: ctx.Ct);
        }
        else
        {
            ctx.Step($"git 克隆 {ins.Repo} …");
            var a = new List<string> { "clone", "--depth", "1" };
            if (ins.Branch != null) { a.Add("--branch"); a.Add(ins.Branch); }
            a.Add(ins.Repo);
            a.Add(dest);
            r = await Proc.RunAsync("git", a, ct: ctx.Ct);
        }
        if (!r.Ok) return StepOutcome.Fail($"git exit {r.ExitCode}");

        foreach (var p in ins.Path ?? new List<string>())
            EnvPath.AddToUserPath(ctx.Path.Resolve(p));
        return StepOutcome.Done();
    }
}

public sealed class ExeInstaller : IInstaller
{
    public string Method => "exe";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Url is null) return StepOutcome.Fail("exe 需要 url");

        var dlDir = ctx.DownloadDir ?? System.IO.Path.GetTempPath();
        Directory.CreateDirectory(dlDir);
        var tmp = System.IO.Path.Combine(dlDir, $"{item.Id}-installer.exe");
        ctx.Step($"下载安装包 {ins.Url} …");
        await Download.ToFileAsync(ins.Url, tmp, ctx, ctx.Ct);

        ctx.Step("运行安装程序 …");
        var args = string.IsNullOrWhiteSpace(ins.Args)
            ? Array.Empty<string>()
            : ins.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var r = await Proc.RunAsync(tmp, args, ct: ctx.Ct);

        // Keep the installer in the configured download dir; only clean the temp copy.
        if (ctx.DownloadDir == null) try { File.Delete(tmp); } catch { /* best effort */ }
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"安装程序退出码 {r.ExitCode}");
    }
}

/// <summary>Installs from a repo-bundled package (e.g. a large .7z dropped into assets/packages/) when
/// present: extract if archived, then run the contained installer (UAC-aware). If the package isn't
/// present, falls back to a manual download (reported as a skip) so a bare repo still works.</summary>
public sealed class LocalInstaller : IInstaller
{
    public string Method => "local";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (string.IsNullOrWhiteSpace(ins.LocalPackage))
            return StepOutcome.Skip("未配置本地安装包，请前往官网手动下载");

        var pkg = FindPackage(ctx, ins.LocalPackage);
        if (pkg == null)
        {
            ctx.Step("未发现本地安装包，请前往官网手动下载");
            return StepOutcome.Skip("未发现本地安装包，请前往官网手动下载");
        }
        ctx.Step($"发现本地安装包：{System.IO.Path.GetFileName(pkg)}");

        var installer = pkg;
        var lower = pkg.ToLowerInvariant();
        if (lower.EndsWith(".7z") || lower.EndsWith(".zip"))
        {
            var tmpEx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"windeploy_{item.Id}_pkg");
            if (Directory.Exists(tmpEx)) Directory.Delete(tmpEx, true);
            if (lower.EndsWith(".7z"))
            {
                var seven = PortableInstaller.Find7z();
                if (seven == null) return StepOutcome.Fail("解压 .7z 需要 7-Zip，请先安装 7-Zip 后重试");
                ctx.Step("用 7-Zip 解压安装包 …");
                var ex = await Proc.RunAsync(seven, new[] { "x", "-o" + tmpEx, pkg, "-y" }, ct: ctx.Ct);
                if (!ex.Ok) return StepOutcome.Fail($"7-Zip 解压失败 退出码 {ex.ExitCode}");
            }
            else
            {
                ctx.Step("解压安装包 …");
                ZipFile.ExtractToDirectory(pkg, tmpEx);
            }
            installer = FindInstallerExe(tmpEx) ?? "";
            if (installer.Length == 0) return StepOutcome.Fail("解压后未找到安装程序 .exe");
            ctx.Step($"安装程序：{System.IO.Path.GetFileName(installer)}");
        }
        else if (!lower.EndsWith(".exe") && !lower.EndsWith(".msi"))
        {
            return StepOutcome.Fail($"不支持的本地安装包类型：{System.IO.Path.GetExtension(pkg)}");
        }

        ctx.Step("运行安装程序（如出现 UAC 提示请允许）…");
        try
        {
            var psi = new ProcessStartInfo(installer) { UseShellExecute = true };   // honor installer's UAC manifest
            if (!string.IsNullOrWhiteSpace(ins.Args)) psi.Arguments = ins.Args;
            var p = Process.Start(psi);
            if (p == null) return StepOutcome.Fail("无法启动安装程序");
            await p.WaitForExitAsync(ctx.Ct);
            return p.ExitCode == 0 ? StepOutcome.Done("已运行本地安装包") : StepOutcome.Fail($"安装程序退出码 {p.ExitCode}");
        }
        catch (Exception ex) { return StepOutcome.Fail("运行安装程序失败：" + ex.Message); }
    }

    /// <summary>Resolve a repo-relative glob ("assets/packages/VMware-*.7z") to the newest matching file.</summary>
    private static string? FindPackage(EngineContext ctx, string globRel)
    {
        var rel = globRel.Replace('/', System.IO.Path.DirectorySeparatorChar);
        var dir = ctx.ResolveRepo(System.IO.Path.GetDirectoryName(rel) ?? "");
        var pattern = System.IO.Path.GetFileName(rel);
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, pattern).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string? FindInstallerExe(string root)
        => Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
            .FirstOrDefault();
}

/// <summary>For software that can't be auto-installed (no winget / unstable URL): the user downloads it
/// from the homepage themselves. Reported as a skip so batch installs don't fail.</summary>
public sealed class ManualInstaller : IInstaller
{
    public string Method => "manual";

    public Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        ctx.Step("需从官网手动下载安装");
        return Task.FromResult(StepOutcome.Skip("请前往官网手动下载"));
    }
}

/// <summary>Apps installed by picking a specific GitHub release tag + asset interactively (handled in the
/// GUI). In a non-interactive batch run there's nothing to pick, so it's reported as a skip.</summary>
public sealed class GithubReleaseInstaller : IInstaller
{
    public string Method => "github-release";

    public Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        ctx.Step("请在软件详情页选择发布版本与文件后安装");
        return Task.FromResult(StepOutcome.Skip("请在详情页选择版本下载安装"));
    }
}

public sealed class CondaInstaller : IInstaller
{
    public string Method => "conda";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.EnvFile is null) return StepOutcome.Fail("conda needs envFile");
        var conda = CommandFinder.Find("conda") ?? CommandFinder.Find("mamba");
        if (conda is null) return StepOutcome.Fail("conda not found on PATH");

        var a = new List<string> { "env", "create", "-f", ctx.ResolveRepo(ins.EnvFile) };
        if (ins.EnvName != null) { a.Add("-n"); a.Add(ins.EnvName); }
        var r = await Proc.RunAsync(conda, a, ct: ctx.Ct);
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"conda exit {r.ExitCode}");
    }
}

public sealed class VscodeExtInstaller : IInstaller
{
    public string Method => "vscode-ext";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var rel = item.Install.Extensions;
        if (rel is null) return StepOutcome.Fail("vscode-ext needs extensions file");
        var file = ctx.ResolveRepo(rel);
        if (!File.Exists(file)) return StepOutcome.Skip("extensions list missing");
        var code = CommandFinder.Find("code");
        if (code is null) return StepOutcome.Skip("code CLI not found");

        var exts = (await File.ReadAllLinesAsync(file, ctx.Ct))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        var failed = 0;
        foreach (var ext in exts)
        {
            var r = await Proc.RunAsync(code, new[] { "--install-extension", ext, "--force" }, ct: ctx.Ct);
            if (!r.Ok) failed++;
        }
        return failed == 0
            ? StepOutcome.Done($"{exts.Count} extensions")
            : StepOutcome.Fail($"{failed}/{exts.Count} failed");
    }
}

public sealed class ScriptInstaller : IInstaller
{
    public string Method => "script";

    public async Task<StepOutcome> RunAsync(CatalogItem item, EngineContext ctx)
    {
        var rel = item.Install.Run;
        if (rel is null) return StepOutcome.Fail("script needs run");
        var file = ctx.ResolveRepo(rel);
        if (!File.Exists(file)) return StepOutcome.Fail("script missing");
        var r = await Proc.RunAsync(file, Array.Empty<string>(), ct: ctx.Ct); // Proc wraps .ps1
        return r.Ok ? StepOutcome.Done() : StepOutcome.Fail($"script exit {r.ExitCode}");
    }
}
