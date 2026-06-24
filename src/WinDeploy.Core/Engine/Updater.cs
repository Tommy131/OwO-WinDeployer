using System.Diagnostics;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>Updates an already-installed item to the latest version.
/// winget → <c>winget upgrade</c>; git → <c>git pull</c>; portable/vscode-ext → re-run the installer
/// (idempotent, fetches the latest). "No applicable upgrade" is reported as success, not failure.</summary>
public static class Updater
{
    public static readonly string[] SupportedMethods = { "winget", "winget-bundle", "git", "portable", "vscode-ext" };

    public static bool CanUpdate(CatalogItem item) => SupportedMethods.Contains(item.Install.Method);

    public static async Task<StepOutcome> UpdateAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        ctx.Step(Localizer.T("engine.update.checking"));
        switch (ins.Method)
        {
            case "winget" when ins.Id != null:
                return Interpret(await Upgrade(ins.Id, ctx));

            case "winget-bundle" when ins.Ids is { Count: > 0 } ids:
            {
                var failed = new List<string>();
                var changed = 0;
                foreach (var id in ids)
                {
                    var o = Interpret(await Upgrade(id, ctx));
                    if (o.Status == StepStatus.Failed) failed.Add(id);
                    else if (o.Message == Updated) changed++;
                }
                if (failed.Count > 0) return StepOutcome.Fail(Localizer.Format("engine.update.failed", string.Join(", ", failed)));
                return StepOutcome.Done(changed > 0 ? Localizer.Format("engine.update.updatedCount", changed) : UpToDate);
            }

            case "git" when ins.Repo != null && ins.Dest != null:
            {
                var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.Dest);
                if (!Directory.Exists(System.IO.Path.Combine(dest, ".git")))
                    return StepOutcome.Fail(Localizer.T("engine.update.noRepo"));
                var r = await Proc.RunAsync("git", new[] { "-C", dest, "pull", "--ff-only" }, ct: ctx.Ct);
                if (!r.Ok) return StepOutcome.Fail(Localizer.Format("engine.update.gitPullExit", r.ExitCode));
                // MATCHED: git's own stdout (EN + zh-CN), not user-facing — do not localize these literals.
                var same = r.StdOut.Contains("up to date", StringComparison.OrdinalIgnoreCase)
                           || r.StdOut.Contains("已经是最新");
                return StepOutcome.Done(same ? UpToDate : Updated);
            }

            case "portable" when ins.Url != null && ins.ExtractTo != null:
            {
                var o = await new PortableInstaller().RunAsync(item, ctx);
                return o.Status == StepStatus.Ok ? StepOutcome.Done(Localizer.T("engine.update.portableRefetched")) : o;
            }

            case "vscode-ext":
                return await new VscodeExtInstaller().RunAsync(item, ctx);

            default:
                return StepOutcome.Fail(Localizer.T("engine.update.unsupported"));
        }
    }

    /// <summary>Install a specific (older) version over the installed one — winget only.</summary>
    public static async Task<StepOutcome> DowngradeAsync(CatalogItem item, EngineContext ctx)
    {
        var ins = item.Install;
        if (ins.Method != "winget" || ins.Id == null) return StepOutcome.Fail(Localizer.T("engine.update.onlyWingetDowngrade"));
        var v = item.Version;
        if (string.IsNullOrEmpty(v)) return StepOutcome.Fail(Localizer.T("engine.update.noTargetVersion"));
        ctx.Step(Localizer.Format("engine.update.downgradeTo", v));
        var r = await Proc.RunAsync("winget", new[]
        {
            "install", "--id", ins.Id, "-e", "--version", v, "--force",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
        }, ct: ctx.Ct);
        return r.Ok ? StepOutcome.Done(Localizer.Format("engine.update.downgraded", v)) : StepOutcome.Fail(Localizer.Format("engine.update.downgradeExit", r.ExitCode));
    }

    // Emitted result messages shown to the user. NOT the winget-stdout matchers below ("已是最新" etc.),
    // which stay Chinese because they are compared against winget's localized output on a zh-CN machine.
    private static string Updated => Localizer.T("engine.update.updated");
    private static string UpToDate => Localizer.T("engine.update.upToDate");

    private static Task<ProcResult> Upgrade(string id, EngineContext ctx)
    {
        var sw = Stopwatch.StartNew();
        string? last = null;
        return Proc.RunStreamingAsync("winget", new[]
        {
            // --include-unknown: upgrade packages whose installed version winget can't determine
            // (e.g. Sublime Merge), which winget otherwise refuses to touch.
            "upgrade", "--id", id, "-e", "--include-unknown",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
        }, tok => last = WingetProgress.Handle(tok, ctx, sw, last), ct: ctx.Ct);
    }

    /// <summary>winget upgrade exits non-zero when nothing applies; treat that as "已是最新", not failure.</summary>
    private static StepOutcome Interpret(ProcResult r)
    {
        if (r.Ok) return StepOutcome.Done(Updated);
        // MATCHED: winget's own stdout (EN + zh-CN localized). These literals are compared against external
        // tool output — do NOT localize them or update detection breaks on Chinese Windows.
        var noUpgrade =
            r.StdOut.Contains("No applicable", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("No newer package", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("已是最新", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("没有可用", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("没有适用", StringComparison.OrdinalIgnoreCase);
        if (noUpgrade) return StepOutcome.Done(UpToDate);

        // Stale upstream manifest (e.g. Unity Hub's non-versioned download URL) → winget rejects the
        // download. There is no supported param to bypass the hash; surface it as a source issue.
        // MATCHED: "哈希" / "hash"+"match" are winget stdout tokens — not user-facing — leave untranslated.
        var hashMismatch = r.StdOut.Contains("哈希", StringComparison.OrdinalIgnoreCase)
            || (r.StdOut.Contains("hash", StringComparison.OrdinalIgnoreCase) && r.StdOut.Contains("match", StringComparison.OrdinalIgnoreCase));
        if (hashMismatch) return StepOutcome.Fail(Localizer.T("engine.update.hashMismatch"));

        return StepOutcome.Fail(Localizer.Format("engine.update.wingetUpgradeExit", r.ExitCode));
    }
}
