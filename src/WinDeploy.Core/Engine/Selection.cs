using WinDeploy.Core.Models;

namespace WinDeploy.Core.Engine;

/// <summary>Resolves which catalog items to act on, from a profile and/or CLI flags.</summary>
public static class Selection
{
    public static List<CatalogItem> Resolve(Catalog cat, Profile? profile,
        IReadOnlyCollection<string>? only, bool all, string? category)
    {
        var sel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (only is { Count: > 0 })
        {
            foreach (var i in cat.Items)
                if (only.Contains(i.Id)) sel.Add(i.Id);
        }
        else if (all)
        {
            foreach (var i in cat.Items) sel.Add(i.Id);
        }
        else if (category != null)
        {
            foreach (var i in cat.Items)
                if (string.Equals(i.Category, category, StringComparison.OrdinalIgnoreCase)) sel.Add(i.Id);
        }
        else if (profile != null)
        {
            ApplyProfile(cat, profile, sel);
        }
        else
        {
            foreach (var i in cat.Items)
                if (i.Default) sel.Add(i.Id);
        }

        return cat.Items.Where(i => sel.Contains(i.Id)).ToList();
    }

    private static void ApplyProfile(Catalog cat, Profile p, HashSet<string> sel)
    {
        foreach (var token in p.Select)
        {
            if (token.StartsWith("@category:", StringComparison.OrdinalIgnoreCase))
            {
                var c = token["@category:".Length..];
                foreach (var i in cat.Items)
                    if (string.Equals(i.Category, c, StringComparison.OrdinalIgnoreCase)) sel.Add(i.Id);
            }
            else if (token.Equals("@all", StringComparison.OrdinalIgnoreCase))
                foreach (var i in cat.Items) sel.Add(i.Id);
            else if (token.Equals("@default", StringComparison.OrdinalIgnoreCase))
                foreach (var i in cat.Items) { if (i.Default) sel.Add(i.Id); }
            else
                sel.Add(token);
        }
        foreach (var d in p.Deselect) sel.Remove(d);
    }
}
