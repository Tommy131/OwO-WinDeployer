using System.Text.Json;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Config;

/// <summary>Applies configs/env/env.json: user environment variables and PATH entries.</summary>
public static class EnvApplier
{
    public static ConfigResult Apply(string repoRoot, PathResolver pr)
    {
        var file = Path.Combine(repoRoot, "configs", "env", "env.json");
        if (!File.Exists(file)) return ConfigResult.Skip(Localizer.T("engine.env.name"), Localizer.T("engine.env.noEnvJson"));

        EnvConfig env;
        try { env = JsonSerializer.Deserialize<EnvConfig>(File.ReadAllText(file), CatalogLoader.Json) ?? new(); }
        catch (Exception ex) { return ConfigResult.Fail(Localizer.T("engine.env.name"), ex.Message); }

        foreach (var kv in env.Vars) EnvPath.SetUserVar(kv.Key, pr.Resolve(kv.Value));
        var added = env.Path.Count(p => EnvPath.AddToUserPath(pr.Resolve(p)));
        return ConfigResult.Ok(Localizer.T("engine.env.name"), Localizer.Format("engine.env.summary", env.Vars.Count, added));
    }
}
