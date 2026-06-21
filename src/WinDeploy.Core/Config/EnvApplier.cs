using System.Text.Json;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Config;

/// <summary>Applies configs/env/env.json: user environment variables and PATH entries.</summary>
public static class EnvApplier
{
    public static ConfigResult Apply(string repoRoot, PathResolver pr)
    {
        var file = Path.Combine(repoRoot, "configs", "env", "env.json");
        if (!File.Exists(file)) return ConfigResult.Skip("环境变量", "无 env.json");

        EnvConfig env;
        try { env = JsonSerializer.Deserialize<EnvConfig>(File.ReadAllText(file), CatalogLoader.Json) ?? new(); }
        catch (Exception ex) { return ConfigResult.Fail("环境变量", ex.Message); }

        foreach (var kv in env.Vars) EnvPath.SetUserVar(kv.Key, pr.Resolve(kv.Value));
        var added = env.Path.Count(p => EnvPath.AddToUserPath(pr.Resolve(p)));
        return ConfigResult.Ok("环境变量", $"{env.Vars.Count} 变量 · 新增 {added} 个 PATH");
    }
}
