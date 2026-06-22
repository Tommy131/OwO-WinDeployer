using System.Text;
using System.Text.Json;
using WinDeploy.Core.Util;

namespace WinDeploy.Core.Engine;

/// <summary>One installed program as seen in Add/Remove Programs (ARP).</summary>
public sealed record InstalledProgram(
    string Name, string? Version, string? Publisher, string? InstallDate, long SizeKb);

/// <summary>Reads the machine's installed-software inventory from the uninstall registry (via PowerShell,
/// so Core stays free of a Windows-only registry dependency) and exports it as CSV / JSON / HTML for asset
/// management and audits.</summary>
public static class Inventory
{
    private const string Ps = """
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $paths = @(
          'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
        Get-ItemProperty $paths -ErrorAction SilentlyContinue |
          Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
          Select-Object DisplayName,DisplayVersion,Publisher,InstallDate,EstimatedSize |
          Sort-Object DisplayName |
          ConvertTo-Json -Compress
        """;

    public static async Task<List<InstalledProgram>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<InstalledProgram>();
        ProcResult r;
        try { r = await Proc.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", Ps }, ct: ct); }
        catch { return list; }
        if (string.IsNullOrWhiteSpace(r.StdOut)) return list;

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var e in root.EnumerateArray()) Add(e, list);
            else if (root.ValueKind == JsonValueKind.Object)
                Add(root, list);
        }
        catch { /* unparseable → empty */ }

        return list
            .GroupBy(p => (p.Name, p.Version))   // de-dup identical rows across hives
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Add(JsonElement e, List<InstalledProgram> list)
    {
        var name = Str(e, "DisplayName");
        if (string.IsNullOrWhiteSpace(name)) return;
        list.Add(new InstalledProgram(
            name!, Str(e, "DisplayVersion"), Str(e, "Publisher"),
            FormatDate(Str(e, "InstallDate")), Num(e, "EstimatedSize")));
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string? FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Length == 8 && long.TryParse(raw, out _)
            ? $"{raw[..4]}-{raw[4..6]}-{raw[6..]}" : raw;
    }

    public static string ToCsv(IEnumerable<InstalledProgram> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("名称,版本,发布者,安装日期,大小(MB)");
        foreach (var p in items)
            sb.AppendLine(string.Join(",",
                Csv(p.Name), Csv(p.Version), Csv(p.Publisher), Csv(p.InstallDate), Mb(p.SizeKb)));
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<InstalledProgram> items)
        => JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });

    public static string ToHtml(IEnumerable<InstalledProgram> items)
    {
        var list = items.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"zh\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>软件清单</title><style>");
        sb.AppendLine("body{font-family:Segoe UI,system-ui,sans-serif;margin:32px;color:#1b1b1a}");
        sb.AppendLine("h1{font-size:20px}table{border-collapse:collapse;width:100%;font-size:13px}");
        sb.AppendLine("th,td{border:1px solid #e6e6e1;padding:7px 10px;text-align:left}");
        sb.AppendLine("th{background:#f5f5f2}tr:nth-child(even){background:#fafaf8}</style></head><body>");
        sb.AppendLine($"<h1>软件清单 · {Environment.MachineName} · 共 {list.Count} 项</h1>");
        sb.AppendLine("<table><tr><th>名称</th><th>版本</th><th>发布者</th><th>安装日期</th><th>大小(MB)</th></tr>");
        foreach (var p in list)
            sb.AppendLine($"<tr><td>{H(p.Name)}</td><td>{H(p.Version)}</td><td>{H(p.Publisher)}</td><td>{H(p.InstallDate)}</td><td>{Mb(p.SizeKb)}</td></tr>");
        sb.AppendLine("</table></body></html>");
        return sb.ToString();
    }

    private static string Mb(long kb) => kb > 0 ? (kb / 1024.0).ToString("0.0") : "";
    private static string Csv(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
