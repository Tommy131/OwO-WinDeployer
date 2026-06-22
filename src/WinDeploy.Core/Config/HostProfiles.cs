using System.Text.Json;

namespace WinDeploy.Core.Config;

/// <summary>Maps a machine name → default profile, so a workstation, a laptop and a VM can each pull the
/// right preset without the operator choosing one. Stored as catalog/hosts.json (data, travels in repo):
/// <code>{ "DEVBOX": "dev", "LAPTOP-*": "laptop-lite", "*": "full" }</code>
/// Keys may be exact names or contain a single <c>*</c> wildcard; first match wins, <c>*</c> is the fallback.</summary>
public static class HostProfiles
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string FilePath(string catalogDir) => Path.Combine(catalogDir, "hosts.json");

    /// <summary>Resolve the profile name for <paramref name="machine"/> (defaults to this machine), or null.</summary>
    public static string? Resolve(string catalogDir, string? machine = null)
    {
        var path = FilePath(catalogDir);
        if (!File.Exists(path)) return null;
        machine ??= Environment.MachineName;

        Dictionary<string, string>? map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
        if (map is null) return null;

        // Exact match first.
        foreach (var (k, v) in map)
            if (!k.Contains('*') && string.Equals(k, machine, StringComparison.OrdinalIgnoreCase))
                return v;
        // Then wildcard patterns (excluding the bare "*").
        foreach (var (k, v) in map)
            if (k.Contains('*') && k != "*" && WildcardMatch(k, machine))
                return v;
        // Finally the catch-all.
        return map.TryGetValue("*", out var fallback) ? fallback : null;
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
