using System.Text.Json;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.Core.Config;

/// <summary>The reinstall/migration helper: bundle the repo's <c>configs/</c> plus a manifest of which
/// catalog apps are currently installed into a portable kit folder (USB-friendly). On the fresh machine,
/// <c>import</c> copies the configs back and tells you the exact <c>apply --only …</c> command to replay.</summary>
public static class MigrationKit
{
    public sealed class Manifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string? Machine { get; set; }
        public string? CreatedAt { get; set; }
        public string? OsVersion { get; set; }
        public List<string> InstalledIds { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Build a kit at <paramref name="kitDir"/>: copy configs/ and write manifest.json + RESTORE.txt.</summary>
    public static async Task<List<ConfigResult>> ExportAsync(Catalog catalog, EngineContext ctx, string kitDir,
        Func<CatalogItem, Task<bool>> isInstalled)
    {
        var results = new List<ConfigResult>();
        Directory.CreateDirectory(kitDir);

        // 1) configs/
        var srcConfigs = Path.Combine(ctx.RepoRoot, "configs");
        if (Directory.Exists(srcConfigs))
        {
            var dstConfigs = Path.Combine(kitDir, "configs");
            CopyDir(srcConfigs, dstConfigs);
            results.Add(ConfigResult.Ok(Localizer.T("engine.migrate.configsName"), Localizer.Format("engine.migrate.configsCopied", dstConfigs)));
        }
        else results.Add(ConfigResult.Skip(Localizer.T("engine.migrate.configsName"), Localizer.T("engine.migrate.noConfigsRepo")));

        // 2) manifest of installed catalog apps
        var installed = new List<string>();
        foreach (var it in catalog.Items)
            if (await isInstalled(it)) installed.Add(it.Id);

        var manifest = new Manifest
        {
            Machine = Environment.MachineName,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            OsVersion = Environment.OSVersion.VersionString,
            InstalledIds = installed,
        };
        File.WriteAllText(Path.Combine(kitDir, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
        results.Add(ConfigResult.Ok(Localizer.T("engine.migrate.inventoryName"), Localizer.Format("engine.migrate.inventoryRecorded", installed.Count)));

        // 3) restore instructions
        var restore = Localizer.Format("engine.migrate.restoreText",
            manifest.Machine, manifest.CreatedAt, string.Join(",", installed));
        File.WriteAllText(Path.Combine(kitDir, "RESTORE.txt"), restore);
        return results;
    }

    /// <summary>Restore a kit into the repo: copy its configs/ back over the repo's configs/. Returns the
    /// parsed manifest so the caller can offer to replay the install list.</summary>
    public static (List<ConfigResult> Results, Manifest? Manifest) Import(string kitDir, string repoRoot)
    {
        var results = new List<ConfigResult>();
        var kitConfigs = Path.Combine(kitDir, "configs");
        if (Directory.Exists(kitConfigs))
        {
            CopyDir(kitConfigs, Path.Combine(repoRoot, "configs"));
            results.Add(ConfigResult.Ok(Localizer.T("engine.migrate.configsName"), Localizer.T("engine.migrate.configsRestored")));
        }
        else results.Add(ConfigResult.Skip(Localizer.T("engine.migrate.configsName"), Localizer.T("engine.migrate.noConfigsKit")));

        Manifest? manifest = null;
        var mf = Path.Combine(kitDir, "manifest.json");
        if (File.Exists(mf))
        {
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf), Json); } catch { /* ignore */ }
            if (manifest != null)
                results.Add(ConfigResult.Ok(Localizer.T("engine.migrate.inventoryName"), Localizer.Format("engine.migrate.manifestFrom", manifest.Machine, manifest.InstalledIds.Count)));
        }
        return (results, manifest);
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
