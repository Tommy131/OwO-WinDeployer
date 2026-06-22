using System.Text.RegularExpressions;

namespace WinDeploy.Core.Config;

/// <summary>
/// Best-effort redaction of secrets when capturing configs into the repo, so tokens/passwords
/// never land in version control. Not a substitute for review, but a strong default.
/// </summary>
public static partial class Secrets
{
    private const string Mask = "***REDACTED***";

    /// <summary>Extra key substrings to redact (settable from the GUI settings page).</summary>
    public static string[] ExtraKeywords { get; set; } = Array.Empty<string>();

    // Provider-prefixed token blobs (GitHub, Slack, AWS, OpenAI, …).
    [GeneratedRegex(@"\b(gh[opusr]_|github_pat_|xox[baprs]-|sk-[A-Za-z0-9]|AKIA|ASIA)[A-Za-z0-9_\-]{6,}\b")]
    private static partial Regex TokenBlob();

    // JSON: "<key containing token/secret/password/apikey/auth>": "<value>"
    [GeneratedRegex("(\"[^\"]*(?:token|secret|password|api[_-]?key|access[_-]?key|auth)[^\"]*\"\\s*:\\s*\")([^\"]*)(\")",
        RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    // INI / gitconfig: key = value, where the key looks sensitive.
    [GeneratedRegex(@"(?im)^(\s*[^=\n]*(?:token|secret|password|api[_-]?key|access[_-]?key|oauth)[^=\n]*=\s*)(.+)$")]
    private static partial Regex IniSecret();

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".txt", ".xml", ".yml", ".yaml", ".ini", ".conf", ".config", ".cfg", ".toml", ".env", ".ps1", "",
    };

    // Dotfiles whose basename (not extension) marks them as text so secrets in them get redacted on export.
    private static readonly HashSet<string> TextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitconfig", "config", ".npmrc", ".condarc", ".wslconfig", ".yarnrc", ".netrc",
    };

    public static bool IsTextConfig(string fileName)
        => TextExt.Contains(Path.GetExtension(fileName)) || TextNames.Contains(fileName);

    /// <summary>Returns the redacted text and how many secrets were masked.</summary>
    public static (string Text, int Count) Redact(string content)
    {
        var n = 0;
        content = TokenBlob().Replace(content, _ => { n++; return Mask; });
        content = JsonSecret().Replace(content, m => { n++; return m.Groups[1].Value + Mask + m.Groups[3].Value; });
        content = IniSecret().Replace(content, m => { n++; return m.Groups[1].Value + Mask; });

        foreach (var kw in ExtraKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var re = new Regex("(\"[^\"]*" + Regex.Escape(kw) + "[^\"]*\"\\s*:\\s*\")([^\"]*)(\")", RegexOptions.IgnoreCase);
            content = re.Replace(content, m => { n++; return m.Groups[1].Value + Mask + m.Groups[3].Value; });
        }
        return (content, n);
    }
}
