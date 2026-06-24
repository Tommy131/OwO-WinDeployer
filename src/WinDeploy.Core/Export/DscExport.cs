using System.Text;
using WinDeploy.Core.I18n;
using WinDeploy.Core.Models;

namespace WinDeploy.Core.Export;

/// <summary>Serializes a selection into a native <c>winget configure</c> (DSC) YAML, so the same software
/// set can be deployed unattended through winget/Intune/GPO/SCCM without this tool's exe. Only winget and
/// winget-bundle items are expressible as WinGetPackage resources; others are listed as comments.</summary>
public static class DscExport
{
    public static string Build(IEnumerable<CatalogItem> items)
    {
        var list = items.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# yaml-language-server: $schema=https://aka.ms/configuration-dsc-schema/0.2");
        sb.AppendLine(Localizer.T("engine.dsc.comment.header"));
        sb.AppendLine("properties:");
        sb.AppendLine("  configurationVersion: 0.2.0");
        sb.AppendLine("  resources:");

        var any = false;
        var skipped = new List<string>();
        foreach (var it in list)
        {
            switch (it.Install.Method)
            {
                case "winget" when !string.IsNullOrWhiteSpace(it.Install.Id):
                    AppendPackage(sb, Sanitize(it.Id), it.Install.Id!, it.Name, it.Install.Source, it.Version);
                    any = true;
                    break;
                case "winget-bundle" when it.Install.Ids is { Count: > 0 } ids:
                    var n = 0;
                    foreach (var wid in ids)
                    {
                        AppendPackage(sb, $"{Sanitize(it.Id)}_{n++}", wid, $"{it.Name} ({wid})", null, null);
                        any = true;
                    }
                    break;
                default:
                    skipped.Add($"{it.Id} ({it.Install.Method})");
                    break;
            }
        }

        if (!any) sb.AppendLine("    []");
        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Localizer.T("engine.dsc.comment.skipped"));
            foreach (var s in skipped) sb.AppendLine("#   - " + s);
        }
        return sb.ToString();
    }

    private static void AppendPackage(StringBuilder sb, string resourceId, string wingetId, string desc, string? source, string? version)
    {
        sb.AppendLine("    - resource: Microsoft.WinGet.DSC/WinGetPackage");
        sb.AppendLine($"      id: {resourceId}");
        sb.AppendLine("      directives:");
        sb.AppendLine($"        description: {Escape(Localizer.Format("engine.dsc.comment.install", desc))}");
        sb.AppendLine("        allowPrerelease: false");
        sb.AppendLine("      settings:");
        sb.AppendLine($"        id: {wingetId}");
        sb.AppendLine($"        source: {(string.IsNullOrWhiteSpace(source) ? "winget" : source)}");
        if (!string.IsNullOrWhiteSpace(version)) sb.AppendLine($"        version: \"{version}\"");
        sb.AppendLine("        ensure: Present");
    }

    /// <summary>DSC resource ids must be simple identifiers.</summary>
    private static string Sanitize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var c in id) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        return s.Length == 0 ? "pkg" : s;
    }

    private static string Escape(string s) => s.Replace(":", " -").Replace("\n", " ").Trim();
}
