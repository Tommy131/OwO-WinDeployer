namespace OwOWinDeployer.Core.Util;

/// <summary>Minimal SemVer 2.0.0 precedence comparison — enough for the app self-update check, which must order
/// pre-releases correctly (e.g. <c>1.3.1-rc.1 &lt; 1.3.1-rc.2 &lt; 1.3.1</c>). A leading <c>v</c> and any <c>+build</c>
/// metadata are ignored. The numeric core may have more than three parts (e.g. <c>1.2.3.4</c> patch-of-patch),
/// compared field by field. Build metadata never affects precedence (SemVer §10).</summary>
public static class SemVer
{
    /// <summary>&gt;0 if <paramref name="a"/> is newer, &lt;0 if older, 0 if equal precedence.</summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        for (int i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            long x = i < coreA.Length ? coreA[i] : 0;
            long y = i < coreB.Length ? coreB[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }

        // Cores equal → a version WITH a pre-release is LOWER than one without (SemVer §11.3).
        bool hasA = preA.Length > 0, hasB = preB.Length > 0;
        if (!hasA && !hasB) return 0;
        if (!hasA) return 1;
        if (!hasB) return -1;

        // Both have pre-release identifiers → compare dot-separated, left to right (SemVer §11.4).
        for (int i = 0; i < Math.Max(preA.Length, preB.Length); i++)
        {
            if (i >= preA.Length) return -1;   // a ran out first → smaller set → lower
            if (i >= preB.Length) return 1;
            int cmp = CompareIdentifier(preA[i], preB[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>True if the version string carries a pre-release identifier (e.g. <c>1.3.1-rc.1</c>).</summary>
    public static bool IsPrerelease(string v) => Split(v).pre.Length > 0;

    private static (long[] core, string[] pre) Split(string v)
    {
        var s = v.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int plus = s.IndexOf('+');                 // strip build metadata (ignored for precedence)
        if (plus >= 0) s = s[..plus];

        string corePart, prePart = "";
        int dash = s.IndexOf('-');
        if (dash >= 0) { corePart = s[..dash]; prePart = s[(dash + 1)..]; }
        else corePart = s;

        var core = corePart.Split('.', StringSplitOptions.RemoveEmptyEntries)
                           .Select(p => long.TryParse(p, out var n) ? n : 0L).ToArray();
        var pre = prePart.Length == 0 ? Array.Empty<string>()
                        : prePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return (core, pre);
    }

    private static int CompareIdentifier(string a, string b)
    {
        bool na = long.TryParse(a, out var ia);
        bool nb = long.TryParse(b, out var ib);
        if (na && nb) return ia.CompareTo(ib);     // both numeric → compare numerically
        if (na) return -1;                          // numeric identifiers are lower than alphanumeric
        if (nb) return 1;
        return string.CompareOrdinal(a, b);         // both alphanumeric → ASCII order
    }
}
