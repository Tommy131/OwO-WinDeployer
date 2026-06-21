using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.Models;

namespace WinDeploy.Core.Config;

/// <summary>Copies config files between the repo and the machine, both directions, with backup.</summary>
public static class ConfigSync
{
    private static string Stamp => DateTime.Now.ToString("yyyyMMddHHmmss");

    /// <summary>Repo → machine.</summary>
    public static ConfigResult Apply(CatalogItem item, EngineContext ctx)
    {
        var c = item.Config!;
        if (c.Target is null) return ConfigResult.Skip(item.Name, "未定义 target");

        var src = ctx.ResolveRepo(c.Source ?? $"configs/{item.Id}");
        if (!Directory.Exists(src)) return ConfigResult.Skip(item.Name, "仓库内无配置");

        var target = ctx.Path.Resolve(c.Target);
        Directory.CreateDirectory(target);

        var files = c.Files ?? Directory.GetFiles(src).Select(Path.GetFileName).Where(n => n != null).Cast<string>().ToList();
        var n = 0;
        foreach (var f in files)
        {
            var s = Path.Combine(src, f);
            if (!File.Exists(s)) continue;
            var d = Path.Combine(target, f);
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            if (File.Exists(d)) { try { File.Copy(d, $"{d}.bak.{Stamp}", true); } catch { /* best effort */ } }
            File.Copy(s, d, true);
            n++;
        }
        return n > 0 ? ConfigResult.Ok(item.Name, $"套用 {n} 个文件") : ConfigResult.Skip(item.Name, "无文件可套用");
    }

    /// <summary>Machine → repo (precise: only the declared files).</summary>
    public static ConfigResult Export(CatalogItem item, EngineContext ctx)
    {
        var c = item.Config!;
        if (c.Target is null || c.Files is null) return ConfigResult.Skip(item.Name, "无 files 定义，跳过采集");

        var target = ctx.Path.Resolve(c.Target);
        var dst = ctx.ResolveRepo(c.Source ?? $"configs/{item.Id}");
        Directory.CreateDirectory(dst);

        var n = 0;
        var redacted = 0;
        foreach (var f in c.Files)
        {
            var t = Path.Combine(target, f);
            if (!File.Exists(t)) continue;
            var d = Path.Combine(dst, f);
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);

            if (Secrets.IsTextConfig(f))
            {
                var (text, count) = Secrets.Redact(File.ReadAllText(t));
                File.WriteAllText(d, text);
                redacted += count;
            }
            else File.Copy(t, d, true);
            n++;
        }
        if (n == 0) return ConfigResult.Skip(item.Name, "本机无对应文件");
        return ConfigResult.Ok(item.Name, redacted > 0 ? $"采集 {n} 个文件（已脱敏 {redacted} 处）" : $"采集 {n} 个文件");
    }
}
