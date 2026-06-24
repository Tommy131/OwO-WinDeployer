using System.Globalization;

namespace WinDeploy.Core.I18n;

/// <summary>Supported UI languages and helpers to normalize / detect a language code.
/// Codes are the two-letter lowercase form: <c>zh</c> (中文) | <c>en</c> (English) | <c>de</c> (Deutsch).</summary>
public static class Lang
{
    public const string Zh = "zh";
    public const string En = "en";
    public const string De = "de";

    /// <summary>All supported codes in display order.</summary>
    public static readonly string[] All = { Zh, En, De };

    /// <summary>Native display name for a language (shown in the picker, in its own language).</summary>
    public static string DisplayName(string code) => Normalize(code) switch
    {
        Zh => "中文",
        De => "Deutsch",
        _ => "English",
    };

    /// <summary>Map any culture to a supported code: zh-* → zh, de-* → de, everything else → en.</summary>
    public static string FromCulture(CultureInfo? culture)
        => Normalize(culture?.TwoLetterISOLanguageName);

    /// <summary>Coerce an arbitrary string (code, culture name, or null) to a supported code; defaults to en.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return En;
        var c = code.Trim().ToLowerInvariant();
        // Accept full culture names like "zh-CN" / "de-DE" too.
        if (c.Length > 2) c = c[..2];
        return c switch
        {
            "zh" => Zh,
            "de" => De,
            "en" => En,
            _ => En,
        };
    }
}
