using WinDeploy.Core.Engine;
using WinDeploy.Core.Models;
using Xunit;

namespace WinDeploy.Core.Tests;

public class SelectionTests
{
    private static Catalog SampleCatalog() => new()
    {
        Categories = { "dev", "system", "media" },
        Items =
        {
            new CatalogItem { Id = "git", Name = "Git", Category = "dev", Default = true },
            new CatalogItem { Id = "vscode", Name = "VS Code", Category = "dev", Default = true },
            new CatalogItem { Id = "pwsh", Name = "PowerShell", Category = "system", Default = true },
            new CatalogItem { Id = "vlc", Name = "VLC", Category = "media", Default = false },
            new CatalogItem { Id = "obs", Name = "OBS", Category = "media", Default = false },
        },
    };

    private static string[] Ids(IEnumerable<CatalogItem> items) =>
        items.Select(i => i.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Only_SelectsListedIdsOnly()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, profile: null, only: new[] { "git", "vlc" }, all: false, category: null);
        Assert.Equal(new[] { "git", "vlc" }, Ids(result));
    }

    [Fact]
    public void Only_IgnoresUnknownIds()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, null, new[] { "git", "does-not-exist" }, false, null);
        Assert.Equal(new[] { "git" }, Ids(result));
    }

    [Fact]
    public void All_SelectsEveryItem()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, null, null, all: true, null);
        Assert.Equal(new[] { "git", "obs", "pwsh", "vlc", "vscode" }, Ids(result));
    }

    [Fact]
    public void Category_SelectsMatchingCategoryCaseInsensitive()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, null, null, false, category: "MEDIA");
        Assert.Equal(new[] { "obs", "vlc" }, Ids(result));
    }

    [Fact]
    public void NoFlagsNoProfile_SelectsDefaultsOnly()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, null, null, false, null);
        Assert.Equal(new[] { "git", "pwsh", "vscode" }, Ids(result));
    }

    [Fact]
    public void Profile_CategoryToken_SelectsCategory()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@category:media" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "obs", "vlc" }, Ids(result));
    }

    [Fact]
    public void Profile_AllToken_SelectsEverything()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@all" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "git", "obs", "pwsh", "vlc", "vscode" }, Ids(result));
    }

    [Fact]
    public void Profile_DefaultToken_SelectsDefaults()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@default" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "git", "pwsh", "vscode" }, Ids(result));
    }

    [Fact]
    public void Profile_ExplicitIds_AreSelected()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "git", "vlc" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "git", "vlc" }, Ids(result));
    }

    [Fact]
    public void Profile_Deselect_RemovesAfterSelect()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@all" }, Deselect = { "obs", "vlc" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "git", "pwsh", "vscode" }, Ids(result));
    }

    [Fact]
    public void Profile_MixedTokens_UnionThenDeselect()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@category:dev", "vlc" }, Deselect = { "vscode" } };
        var result = Selection.Resolve(cat, profile, null, false, null);
        Assert.Equal(new[] { "git", "vlc" }, Ids(result));
    }

    [Fact]
    public void ResultPreservesCatalogOrder()
    {
        var cat = SampleCatalog();
        var result = Selection.Resolve(cat, null, new[] { "vlc", "git", "pwsh" }, false, null);
        // Resolve returns items in catalog declaration order, not the order requested.
        Assert.Equal(new[] { "git", "pwsh", "vlc" }, result.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void FlagPrecedence_OnlyBeatsAllAndCategoryAndProfile()
    {
        var cat = SampleCatalog();
        var profile = new Profile { Name = "p", Select = { "@all" } };
        var result = Selection.Resolve(cat, profile, only: new[] { "git" }, all: true, category: "media");
        Assert.Equal(new[] { "git" }, Ids(result));
    }
}
