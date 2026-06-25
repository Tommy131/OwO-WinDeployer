using System.Text.Json;
using WinDeploy.Core.I18n;
using Xunit;

namespace WinDeploy.Core.Tests;

/// <summary>
/// Mirrors scripts/check-i18n.ps1: the en/zh/de translation dictionaries must share an identical
/// key set, and placeholder index sets ({0}{1}…) must match per key across the three languages.
/// Reads the dictionaries straight from the embedded resources of WinDeploy.Core (same source the
/// Localizer loads at runtime), so the test is independent of the build's working directory.
/// </summary>
public class I18nAlignmentTests
{
    private static readonly string[] Langs = { "en", "zh", "de" };

    private static Dictionary<string, string> LoadLang(string code)
    {
        var asm = typeof(Localizer).Assembly;
        var marker = $".Resources.{code}.";
        var opts = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            using var s = asm.GetManifestResourceStream(name)!;
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            var part = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes, opts);
            if (part is null) continue;
            foreach (var kv in part) merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    private static IEnumerable<int> Placeholders(string value)
        => System.Text.RegularExpressions.Regex.Matches(value, @"\{(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .OrderBy(n => n);

    [Fact]
    public void EmbeddedResources_AreNonEmpty()
    {
        foreach (var l in Langs)
            Assert.NotEmpty(LoadLang(l));
    }

    [Fact]
    public void AllLanguages_HaveIdenticalKeySets()
    {
        var maps = Langs.ToDictionary(l => l, LoadLang);
        var en = maps["en"];

        var failures = new List<string>();
        foreach (var l in Langs)
        {
            foreach (var k in en.Keys)
                if (!maps[l].ContainsKey(k)) failures.Add($"MISSING [{l}] {k}");
            foreach (var k in maps[l].Keys)
                if (!en.ContainsKey(k)) failures.Add($"EXTRA [{l}] {k} (not in en)");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void PlaceholderIndexSets_MatchAcrossLanguages()
    {
        var maps = Langs.ToDictionary(l => l, LoadLang);
        var en = maps["en"];

        var failures = new List<string>();
        foreach (var k in en.Keys)
        {
            var refSet = string.Join(",", Placeholders(en[k]));
            foreach (var l in new[] { "zh", "de" })
            {
                if (!maps[l].TryGetValue(k, out var v)) continue;
                var cur = string.Join(",", Placeholders(v));
                if (cur != refSet)
                    failures.Add($"PLACEHOLDER MISMATCH '{k}' en={{{refSet}}} {l}={{{cur}}}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
