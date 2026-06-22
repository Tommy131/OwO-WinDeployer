using WinDeploy.Core.Engine.Installers;
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
        switch (ins.Method)
        {
            case "winget" when ins.Id != null:
                return Interpret(await Upgrade(ins.Id, ctx.Ct));

            case "winget-bundle" when ins.Ids is { Count: > 0 } ids:
            {
                var failed = new List<string>();
                var changed = 0;
                foreach (var id in ids)
                {
                    var o = Interpret(await Upgrade(id, ctx.Ct));
                    if (o.Status == StepStatus.Failed) failed.Add(id);
                    else if (o.Message == Updated) changed++;
                }
                if (failed.Count > 0) return StepOutcome.Fail("失败: " + string.Join(", ", failed));
                return StepOutcome.Done(changed > 0 ? $"已更新 {changed} 个组件" : UpToDate);
            }

            case "git" when ins.Repo != null && ins.Dest != null:
            {
                var dest = ctx.Path.Resolve(item.InstallPathOverride ?? ins.Dest);
                if (!Directory.Exists(System.IO.Path.Combine(dest, ".git")))
                    return StepOutcome.Fail("本地仓库不存在，请先安装");
                var r = await Proc.RunAsync("git", new[] { "-C", dest, "pull", "--ff-only" }, ct: ctx.Ct);
                if (!r.Ok) return StepOutcome.Fail($"git pull 退出码 {r.ExitCode}");
                var same = r.StdOut.Contains("up to date", StringComparison.OrdinalIgnoreCase)
                           || r.StdOut.Contains("已经是最新");
                return StepOutcome.Done(same ? UpToDate : Updated);
            }

            case "portable" when ins.Url != null && ins.ExtractTo != null:
            {
                var o = await new PortableInstaller().RunAsync(item, ctx);
                return o.Status == StepStatus.Ok ? StepOutcome.Done("已重新拉取最新便携包") : o;
            }

            case "vscode-ext":
                return await new VscodeExtInstaller().RunAsync(item, ctx);

            default:
                return StepOutcome.Fail("该类型暂不支持更新");
        }
    }

    private const string Updated = "已更新";
    private const string UpToDate = "已是最新";

    private static Task<ProcResult> Upgrade(string id, CancellationToken ct) => Proc.RunAsync("winget", new[]
    {
        "upgrade", "--id", id, "-e",
        "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
    }, ct: ct);

    /// <summary>winget upgrade exits non-zero when nothing applies; treat that as "已是最新", not failure.</summary>
    private static StepOutcome Interpret(ProcResult r)
    {
        if (r.Ok) return StepOutcome.Done(Updated);
        var noUpgrade =
            r.StdOut.Contains("No applicable", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("No newer package", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("已是最新", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("没有可用", StringComparison.OrdinalIgnoreCase)
            || r.StdOut.Contains("没有适用", StringComparison.OrdinalIgnoreCase);
        return noUpgrade ? StepOutcome.Done(UpToDate) : StepOutcome.Fail($"winget upgrade 退出码 {r.ExitCode}");
    }
}
