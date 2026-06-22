using System.Text.Json;
using WinDeploy.Core.Engine;
using WinDeploy.Core.Engine.Installers;
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
            results.Add(ConfigResult.Ok("配置目录", $"已复制 configs/ → {dstConfigs}"));
        }
        else results.Add(ConfigResult.Skip("配置目录", "仓库内无 configs/"));

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
        results.Add(ConfigResult.Ok("软件清单", $"记录 {installed.Count} 个已装应用"));

        // 3) restore instructions
        var restore = $"""
            OwO! Win Deployer 迁移工具包
            来源机器：{manifest.Machine} · {manifest.CreatedAt}

            在新机器上还原：
              1) 拉取/克隆 owo-win-deployer 仓库
              2) windeploy migrate import "<此目录>"      （把 configs/ 拷回仓库）
              3) windeploy apply --only {string.Join(",", installed)}
              4) windeploy apply-config                      （套用配置）
            """;
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
            results.Add(ConfigResult.Ok("配置目录", "已将 configs/ 还原到仓库"));
        }
        else results.Add(ConfigResult.Skip("配置目录", "工具包内无 configs/"));

        Manifest? manifest = null;
        var mf = Path.Combine(kitDir, "manifest.json");
        if (File.Exists(mf))
        {
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(mf), Json); } catch { /* ignore */ }
            if (manifest != null)
                results.Add(ConfigResult.Ok("软件清单", $"来自 {manifest.Machine}，{manifest.InstalledIds.Count} 个应用"));
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
