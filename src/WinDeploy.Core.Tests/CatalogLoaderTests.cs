using System.Text.Json;
using WinDeploy.Core;
using WinDeploy.Core.Models;
using Xunit;

namespace WinDeploy.Core.Tests;

/// <summary>
/// Round-trips the public CatalogLoader surface in Core (Load, LoadProfile, FindCatalogDir,
/// ApplyLocalizedSummaries) against a throwaway temp catalog directory.
/// Note: SaveProfile / ProfileExists are not part of WinDeploy.Core's public API, so those
/// were exercised via the serialize-then-LoadProfile round-trip below instead.
/// </summary>
public sealed class CatalogLoaderTests : IDisposable
{
    private readonly string _dir;

    public CatalogLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "windeploy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    private void WriteProfile(string name, Profile p)
    {
        var profilesDir = Path.Combine(_dir, "profiles");
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(Path.Combine(profilesDir, name + ".json"),
            JsonSerializer.Serialize(p, CatalogLoader.Json));
    }

    [Fact]
    public void Profile_SerializeThenLoad_RoundTrips()
    {
        var original = new Profile
        {
            Name = "dev",
            Select = { "@category:dev", "git" },
            Deselect = { "obs" },
        };
        WriteProfile("dev", original);

        var loaded = CatalogLoader.LoadProfile(_dir, "dev");

        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.Select, loaded.Select);
        Assert.Equal(original.Deselect, loaded.Deselect);
    }

    [Fact]
    public void LoadProfile_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => CatalogLoader.LoadProfile(_dir, "nope"));
    }

    [Fact]
    public void Load_ParsesCatalogWithCommentsAndTrailingCommas()
    {
        var path = Path.Combine(_dir, "catalog.json");
        File.WriteAllText(path, """
        {
            // leading comment
            "schemaVersion": 1,
            "categories": ["dev", "system"],
            "items": [
                { "id": "git", "name": "Git", "category": "dev", "default": true,
                  "install": { "method": "winget", "id": "Git.Git" } },
            ],
        }
        """);

        var cat = CatalogLoader.Load(path);

        Assert.Equal(1, cat.SchemaVersion);
        Assert.Equal(new[] { "dev", "system" }, cat.Categories.ToArray());
        var item = Assert.Single(cat.Items);
        Assert.Equal("git", item.Id);
        Assert.True(item.Default);
        Assert.Equal("winget", item.Install.Method);
        Assert.Equal("Git.Git", item.Install.Id);
    }

    [Fact]
    public void FindCatalogDir_LocatesCatalogWalkingUp()
    {
        var catalogDir = Path.Combine(_dir, "catalog");
        Directory.CreateDirectory(catalogDir);
        File.WriteAllText(Path.Combine(catalogDir, "catalog.json"), "{ \"items\": [] }");
        var nested = Path.Combine(_dir, "a", "b", "c");
        Directory.CreateDirectory(nested);

        var found = CatalogLoader.FindCatalogDir(nested);

        Assert.NotNull(found);
        Assert.Equal(
            Path.GetFullPath(catalogDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(found!).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void FindCatalogDir_ReturnsNullWhenAbsent()
    {
        var found = CatalogLoader.FindCatalogDir(_dir);
        Assert.Null(found);
    }

    [Fact]
    public void ApplyLocalizedSummaries_MergesSidecarTranslations()
    {
        var cat = new Catalog
        {
            Items = { new CatalogItem { Id = "git", Summary = "中文摘要" } },
        };
        var i18nDir = Path.Combine(_dir, "i18n");
        Directory.CreateDirectory(i18nDir);
        File.WriteAllText(Path.Combine(i18nDir, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["git"] = "English summary" }));

        CatalogLoader.ApplyLocalizedSummaries(cat, _dir, "en");

        Assert.Equal("English summary", cat.Items[0].SummaryFor("en"));
        Assert.Equal("中文摘要", cat.Items[0].SummaryFor("zh"));
    }

    [Fact]
    public void ApplyLocalizedSummaries_ZhIsNoOp()
    {
        var cat = new Catalog { Items = { new CatalogItem { Id = "git", Summary = "中文摘要" } } };
        CatalogLoader.ApplyLocalizedSummaries(cat, _dir, "zh");
        Assert.Equal("中文摘要", cat.Items[0].SummaryFor("zh"));
    }
}
