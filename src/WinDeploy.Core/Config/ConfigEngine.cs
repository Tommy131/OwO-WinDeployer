using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Config;

/// <summary>Orchestrates config apply (repo → machine) and export (machine → repo).</summary>
public sealed class ConfigEngine
{
    /// <summary>Apply file configs (per applyWhen) plus the env.json variables/PATH.</summary>
    public async Task<List<ConfigResult>> ApplyAsync(Models.Catalog cat, EngineContext ctx,
        Func<CatalogItem, Task<bool>> isInstalled, bool includeAsk)
    {
        var results = new List<ConfigResult>();
        foreach (var item in cat.Items.Where(i => i.Config != null))
        {
            var when = (item.Config!.ApplyWhen ?? "ifInstalled").ToLowerInvariant();
            var go = when switch
            {
                "always" => true,
                "ask" => includeAsk,
                _ => await isInstalled(item),
            };
            if (!go) { results.Add(ConfigResult.Skip(item.Name, $"applyWhen={when}，跳过")); continue; }
            results.Add(ConfigSync.Apply(item, ctx));
        }
        results.Add(EnvApplier.Apply(ctx.RepoRoot, ctx.Path));
        return results;
    }

    /// <summary>Capture installed apps' configs back into the repo (precise files only).</summary>
    public async Task<List<ConfigResult>> ExportAsync(Models.Catalog cat, EngineContext ctx,
        Func<CatalogItem, Task<bool>> isInstalled)
    {
        var results = new List<ConfigResult>();
        foreach (var item in cat.Items.Where(i => i.Config?.Files != null))
        {
            if (!await isInstalled(item)) { results.Add(ConfigResult.Skip(item.Name, "未安装，跳过采集")); continue; }
            results.Add(ConfigSync.Export(item, ctx));
        }

        // VS Code extension list snapshot.
        var vscode = cat.Items.FirstOrDefault(i => i.Config?.Extensions != null);
        if (vscode != null && await isInstalled(vscode))
        {
            var code = CommandFinder.Find("code");
            if (code != null)
            {
                var r = await Proc.RunAsync(code, new[] { "--list-extensions" }, ct: ctx.Ct);
                if (r.Ok)
                {
                    var file = ctx.ResolveRepo(vscode.Config!.Extensions!);
                    var count = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                    await File.WriteAllTextAsync(file,
                        "# VS Code 扩展清单（导出自本机）。以 # 开头或空行会被忽略。" + Environment.NewLine + r.StdOut, ctx.Ct);
                    results.Add(ConfigResult.Ok("VS Code 扩展", $"采集 {count} 个"));
                }
            }
        }

        return results;
    }
}
