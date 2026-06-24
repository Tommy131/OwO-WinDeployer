using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace WinDeploy.Core.I18n;

/// <summary>
/// Process-wide string localization shared by Core, the WPF App, and the CLI.
///
/// Translations live as embedded JSON resources in this assembly
/// (<c>I18n/Resources/strings.{en,zh,de}.json</c>) — flat <c>key → text</c> maps with
/// dotted, namespaced keys (e.g. <c>settings.title</c>). English is always resident as the
/// fallback base; the current language is loaded on demand and swapped on <see cref="SetLanguage"/>.
///
/// Lookups fall back <c>current → en → key</c>: a missing key renders as the key itself, so gaps
/// are obvious on screen instead of blank. Format args use <see cref="CultureInfo.InvariantCulture"/>
/// because we localize <em>text</em>, not number/date formatting policy (and the CLI runs with
/// InvariantGlobalization, so App and CLI agree).
/// </summary>
public static class Localizer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly object Gate = new();

    // English is the canonical key set and the last-resort fallback — load it once and keep it resident.
    private static readonly Dictionary<string, string> _en = LoadEmbedded(Lang.En);
    private static Dictionary<string, string> _current = _en;

    /// <summary>The active language code: <c>zh</c> | <c>en</c> | <c>de</c>. Defaults to English until set.</summary>
    public static string Current { get; private set; } = Lang.En;

    /// <summary>Raised after the active dictionary has been swapped. Handlers that touch UI must
    /// marshal to the UI thread themselves (this may fire on whatever thread called <see cref="SetLanguage"/>).</summary>
    public static event Action? CultureChanged;

    /// <summary>Switch the active language. No-op if unchanged. Loads the new dictionary and raises
    /// <see cref="CultureChanged"/>.</summary>
    public static void SetLanguage(string? code)
    {
        var lang = Lang.Normalize(code);
        lock (Gate)
        {
            if (lang == Current) return;
            Current = lang;
            _current = lang == Lang.En ? _en : LoadEmbedded(lang);
        }
        CultureChanged?.Invoke();
    }

    /// <summary>Translate a key. Fallback: current → en → the key itself.</summary>
    public static string T(string key)
    {
        if (key.Length == 0) return key;
        var cur = _current;
        if (cur.TryGetValue(key, out var v)) return v;
        if (!ReferenceEquals(cur, _en) && _en.TryGetValue(key, out var e)) return e;
        return key;
    }

    /// <summary>Translate a key and <see cref="string.Format(IFormatProvider, string, object?[])"/> it with
    /// positional <c>{0} {1}</c> args (invariant culture). Falls back like <see cref="T"/>.</summary>
    public static string Format(string key, params object?[] args)
    {
        var tmpl = T(key);
        if (args.Length == 0) return tmpl;
        try { return string.Format(CultureInfo.InvariantCulture, tmpl, args); }
        catch (FormatException) { return tmpl; }   // malformed template — show the raw text, never throw
    }

    /// <summary>Whether the current or English dictionary defines this key.</summary>
    public static bool Has(string key)
        => _current.ContainsKey(key) || _en.ContainsKey(key);

    /// <summary>Every known key (union of current + English). Used by the App to seed XAML resources.</summary>
    public static IEnumerable<string> AllKeys()
    {
        var seen = new HashSet<string>(_en.Keys);
        foreach (var k in _current.Keys) seen.Add(k);
        return seen;
    }

    // Resources are split per area under I18n/Resources/&lt;code&gt;/*.json and merged here, so areas can
    // grow independently. The embedded-resource manifest name for Resources/en/settings.json is
    // "WinDeploy.Core.I18n.Resources.en.settings.json" — we match the ".Resources.&lt;code&gt;." segment.
    private static Dictionary<string, string> LoadEmbedded(string code)
    {
        var asm = typeof(Localizer).Assembly;
        var marker = $".Resources.{code}.";
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s is null) continue;
                var bytes = new byte[s.Length];
                s.ReadExactly(bytes);   // single Stream.Read can short-read under single-file publish
                var part = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes, JsonOpts);
                if (part is null) continue;
                foreach (var kv in part) merged[kv.Key] = kv.Value;   // last writer wins
            }
            catch { /* skip a bad resource — never let it crash startup */ }
        }
        return merged;
    }
}
