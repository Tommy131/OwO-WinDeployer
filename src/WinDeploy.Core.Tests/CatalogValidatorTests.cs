using WinDeploy.Core;
using WinDeploy.Core.Models;
using Xunit;

namespace WinDeploy.Core.Tests;

/// <summary>
/// Exercises CatalogValidator.Validate on small hand-built catalogs. repoRoot is pointed at a
/// throwaway temp dir so the filesystem-dependent checks (icons, config sources) simply warn —
/// the assertions below target the structural Error-level issues, which are deterministic.
/// </summary>
public sealed class CatalogValidatorTests : IDisposable
{
    private readonly string _repoRoot;

    public CatalogValidatorTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "windeploy-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_repoRoot)) Directory.Delete(_repoRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private List<CatalogIssue> Validate(Catalog cat) => CatalogValidator.Validate(cat, _repoRoot);

    [Fact]
    public void FlagsItemMissingId()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "", Name = "Nameless", Category = "dev", Install = new() { Method = "winget", Id = "X.Y" } } },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Message.Contains("id"));
    }

    [Fact]
    public void FlagsItemMissingName()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "git", Name = "", Category = "dev", Install = new() { Method = "winget", Id = "Git.Git" } } },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Warn && i.ItemId == "git" && i.Message.Contains("name"));
    }

    [Fact]
    public void FlagsItemMissingMethod()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "git", Name = "Git", Category = "dev", Install = new() { Method = "" } } },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.ItemId == "git" && i.Message.Contains("method"));
    }

    [Fact]
    public void FlagsUnknownMethod()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "git", Name = "Git", Category = "dev", Install = new() { Method = "telepathy" } } },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.ItemId == "git");
    }

    [Fact]
    public void FlagsDuplicateIds()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items =
            {
                new CatalogItem { Id = "git", Name = "Git", Category = "dev", Install = new() { Method = "winget", Id = "Git.Git" } },
                new CatalogItem { Id = "git", Name = "Git2", Category = "dev", Install = new() { Method = "winget", Id = "Git.Git" } },
            },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || i.Message.Contains("重复"));
    }

    [Fact]
    public void FlagsWingetMissingId()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "git", Name = "Git", Category = "dev", Install = new() { Method = "winget", Id = null } } },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.ItemId == "git");
    }

    [Fact]
    public void FlagsDanglingDependency()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items =
            {
                new CatalogItem { Id = "vscode", Name = "VS Code", Category = "dev",
                    Install = new() { Method = "winget", Id = "Microsoft.VisualStudioCode" },
                    Depends = new() { "ghost-dependency" } },
            },
        };
        var issues = Validate(cat);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Message.Contains("ghost-dependency"));
    }

    [Fact]
    public void WellFormedWingetItem_HasNoStructuralErrors()
    {
        var cat = new Catalog
        {
            Categories = { "dev" },
            Items = { new CatalogItem { Id = "git", Name = "Git", Category = "dev", Install = new() { Method = "winget", Id = "Git.Git" } } },
        };
        var issues = Validate(cat);
        // Icons/config-source checks may warn against the temp repoRoot; there must be no Errors.
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
    }
}
