using System.Text.RegularExpressions;

namespace WinDeploy.Core;

/// <summary>
/// Resolves <c>${Var}</c> path variables (from catalog.pathVars) and <c>%ENV%</c> variables,
/// then normalises slashes. Keeps catalogs portable across machines without a fixed D: drive.
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<string, string> _vars;

    public PathResolver(IDictionary<string, string> vars)
        => _vars = new Dictionary<string, string>(vars, StringComparer.OrdinalIgnoreCase);

    public string Resolve(string input)
    {
        var s = Regex.Replace(input, @"\$\{(\w+)\}", m =>
            _vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        s = Environment.ExpandEnvironmentVariables(s);
        return s.Replace('/', Path.DirectorySeparatorChar);
    }

    public IReadOnlyDictionary<string, string> Vars => _vars;
}
