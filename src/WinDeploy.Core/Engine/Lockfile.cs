using System.Text.Json;
using System.Text.Json.Serialization;
using WinDeploy.Core.Models;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>A reproducibility snapshot: catalog item id → exact installed version. Captured on one machine
/// (<c>lock</c>) and replayed on another (<c>apply --locked</c>) so winget items install the SAME version,
/// not merely "latest". Lives at catalog/lock.json so it travels in the repo.</summary>
public sealed class Lockfile
{
    public int SchemaVersion { get; set; } = 1;
    public string? Machine { get; set; }
    public string? CapturedAt { get; set; }

    /// <summary>catalog item id → version string.</summary>
    public Dictionary<string, string> Versions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath(string catalogDir) => Path.Combine(catalogDir, "lock.json");

    public static Lockfile? Load(string catalogDir)
    {
        var path = DefaultPath(catalogDir);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Lockfile>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public void Save(string catalogDir)
        => File.WriteAllText(DefaultPath(catalogDir), JsonSerializer.Serialize(this, Json));

    /// <summary>Apply locked versions onto the catalog (sets <see cref="CatalogItem.Version"/> for winget
    /// items that are pinned). Returns how many items were pinned.</summary>
    public int ApplyTo(Catalog catalog)
    {
        var n = 0;
        foreach (var it in catalog.Items)
            if (it.Install.Method == "winget" && Versions.TryGetValue(it.Id, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                it.Version = v;
                n++;
            }
        return n;
    }

    /// <summary>Capture installed winget versions via `winget export --include-versions`, mapping each
    /// winget PackageIdentifier back to the catalog item that installs it. Portable items keep their
    /// catalog-pinned <c>version</c>.</summary>
    public static async Task<Lockfile> CaptureAsync(Catalog catalog, string capturedAtIso, CancellationToken ct = default)
    {
        var lf = new Lockfile { Machine = Environment.MachineName, CapturedAt = capturedAtIso };
        var byWingetId = catalog.Items
            .Where(i => i.Install.Method == "winget" && !string.IsNullOrWhiteSpace(i.Install.Id))
            .GroupBy(i => i.Install.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var tmp = Path.Combine(Path.GetTempPath(), $"windeploy_lock_{Guid.NewGuid():N}.json");
        try
        {
            var r = await Proc.RunAsync("winget", new[]
            {
                "export", "-o", tmp, "--include-versions",
                "--accept-source-agreements", "--disable-interactivity",
            }, ct: ct);
            if (File.Exists(tmp))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(tmp, ct));
                if (doc.RootElement.TryGetProperty("Sources", out var sources))
                    foreach (var src in sources.EnumerateArray())
                        if (src.TryGetProperty("Packages", out var pkgs))
                            foreach (var p in pkgs.EnumerateArray())
                            {
                                var wid = p.TryGetProperty("PackageIdentifier", out var pi) ? pi.GetString() : null;
                                var ver = p.TryGetProperty("Version", out var pv) ? pv.GetString() : null;
                                if (wid != null && ver != null && byWingetId.TryGetValue(wid, out var item))
                                    lf.Versions[item.Id] = ver;
                            }
            }
        }
        catch { /* winget missing / export failed → versions stays as far as we got */ }
        finally { try { File.Delete(tmp); } catch { /* best effort */ } }

        // Portable items: record the catalog-pinned version (if any) so the lock is self-describing.
        foreach (var it in catalog.Items)
            if (it.Install.Method == "portable" && !string.IsNullOrWhiteSpace(it.Version) && !lf.Versions.ContainsKey(it.Id))
                lf.Versions[it.Id] = it.Version!;

        return lf;
    }
}
