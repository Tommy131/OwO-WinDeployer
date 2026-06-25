using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
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
        if (c.Target is null) return ConfigResult.Skip(item.Name, Localizer.T("engine.cfgsync.noTarget"));

        var src = ctx.ResolveRepo(c.Source ?? $"configs/{item.Id}");
        if (!Directory.Exists(src)) return ConfigResult.Skip(item.Name, Localizer.T("engine.cfgsync.noRepoConfig"));

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
        return n > 0 ? ConfigResult.Ok(item.Name, Localizer.Format("engine.cfgsync.applied", n)) : ConfigResult.Skip(item.Name, Localizer.T("engine.cfgsync.noFiles"));
    }

    /// <summary>Machine → repo (precise: only the declared files). When <paramref name="redact"/> is false the
    /// user opted to export sensitive data as-is, so text configs are copied raw instead of being masked.</summary>
    public static ConfigResult Export(CatalogItem item, EngineContext ctx, bool redact = true)
    {
        var c = item.Config!;
        if (c.Target is null || c.Files is null) return ConfigResult.Skip(item.Name, Localizer.T("engine.cfgsync.noFilesDef"));

        var target = ctx.Path.Resolve(c.Target);
        var dst = ctx.ResolveRepo(c.Source ?? $"configs/{item.Id}");
        Directory.CreateDirectory(dst);

        var n = 0;
        var redacted = 0;
        var files = new List<ConfigFileInfo>();
        foreach (var f in c.Files)
        {
            var t = Path.Combine(target, f);
            if (!File.Exists(t)) continue;
            var d = Path.Combine(dst, f);
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);

            string? text = null;
            if (redact && Secrets.IsTextConfig(f))
            {
                var (redactedText, count) = Secrets.Redact(File.ReadAllText(t));
                File.WriteAllText(d, redactedText);
                redacted += count;
                text = redactedText;
            }
            else
            {
                File.Copy(t, d, true);
                if (Secrets.IsTextConfig(f)) { try { text = File.ReadAllText(d); } catch { /* preview only */ } }
            }
            n++;
            files.Add(new ConfigFileInfo { Path = RepoRel(ctx.RepoRoot, d), Size = new FileInfo(d).Length, Preview = MakePreview(text) });
        }
        if (n == 0) return ConfigResult.Skip(item.Name, Localizer.T("engine.cfgsync.noLocalFile"));
        var msg = redacted > 0 ? Localizer.Format("engine.cfgsync.capturedRedacted", n, redacted) : Localizer.Format("engine.cfgsync.captured", n);
        return ConfigResult.Ok(item.Name, msg, files);
    }

    public static string RepoRel(string repoRoot, string abs)
        => Path.GetRelativePath(repoRoot, abs).Replace('\\', '/');

    public static string MakePreview(string? text)
    {
        if (text is null) return Localizer.T("engine.cfgsync.previewBinary");
        var t = text.Replace("\r\n", "\n").Trim();
        return t.Length > 600 ? t[..600] + " …" : t;
    }
}
