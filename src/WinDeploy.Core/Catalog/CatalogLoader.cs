using System.Text.Json;
using WinDeploy.Core.Models;

namespace WinDeploy.Core;

/// <summary>Loads catalog.json and profile files. JSON allows // comments and trailing commas.</summary>
public static class CatalogLoader
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Catalog Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Catalog>(json, Json)
               ?? throw new InvalidDataException($"Empty or invalid catalog: {path}");
    }

    /// <summary>Merge per-language summary translations from <c>catalog/i18n/&lt;lang&gt;.json</c> (id → summary)
    /// into each item's <see cref="CatalogItem.LocalizedSummary"/>. zh uses catalog.json's own <c>summary</c>
    /// (no file needed). Best-effort: a missing/invalid file leaves items falling back to the zh summary.
    /// Call once per non-zh language after <see cref="Load"/>.</summary>
    public static void ApplyLocalizedSummaries(Catalog cat, string catalogDir, string lang)
    {
        if (lang == "zh") return;
        var path = Path.Combine(catalogDir, "i18n", lang + ".json");
        if (!File.Exists(path)) return;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json);
            if (map == null) return;
            foreach (var it in cat.Items)
                if (map.TryGetValue(it.Id, out var s) && !string.IsNullOrWhiteSpace(s))
                    it.LocalizedSummary[lang] = s;
        }
        catch { /* leave items on the zh summary */ }
    }

    public static Profile LoadProfile(string catalogDir, string name)
    {
        var path = Path.Combine(catalogDir, "profiles", name + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Profile not found: {path}");
        return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json)
               ?? throw new InvalidDataException($"Invalid profile: {path}");
    }

    /// <summary>Walks up from <paramref name="startDir"/> looking for catalog/catalog.json.</summary>
    public static string? FindCatalogDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "catalog", "catalog.json");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "catalog");
            dir = dir.Parent;
        }
        return null;
    }
}
