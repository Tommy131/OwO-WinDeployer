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
