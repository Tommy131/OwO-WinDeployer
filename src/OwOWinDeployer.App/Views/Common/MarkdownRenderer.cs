using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace OwOWinDeployer.App.Views.Common;

/// <summary>A small, dependency-free Markdown → <see cref="FlowDocument"/> renderer, scoped to the subset that
/// appears in GitHub release notes: headings, bullet / ordered lists, <c>**bold**</c>, <c>`code`</c>, blockquotes,
/// horizontal rules, links, bare URLs and pipe tables. Anything it doesn't recognise falls through as plain text —
/// the goal is a readable, themed rendering of the changelog, not a spec-complete parser. Colours come from the
/// app theme via DynamicResource-equivalent lookups so it matches light/dark.</summary>
public static class MarkdownRenderer
{
    public static FlowDocument ToFlowDocument(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = B("TextPrimary"),
            Background = Brushes.Transparent,
            PagePadding = new Thickness(0),
        };
        if (string.IsNullOrWhiteSpace(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Horizontal rule
            if (Regex.IsMatch(line.Trim(), @"^([-*_])\1{2,}$"))
            {
                doc.Blocks.Add(HorizontalRule());
                i++;
                continue;
            }

            // Heading
            var hm = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
            if (hm.Success)
            {
                doc.Blocks.Add(Heading(hm.Groups[1].Value.Length, hm.Groups[2].Value));
                i++;
                continue;
            }

            // Blockquote (collect consecutive '>' lines)
            if (trimmed.StartsWith(">"))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    quote.Add(Regex.Replace(lines[i].TrimStart(), @"^>\s?", ""));
                    i++;
                }
                doc.Blocks.Add(Blockquote(quote));
                continue;
            }

            // Pipe table: a header row with '|' followed by a separator row of ---/:---
            if (line.Contains('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}.*\|"))
            {
                var table = new List<string>();
                while (i < lines.Length && lines[i].Contains('|')) { table.Add(lines[i]); i++; }
                doc.Blocks.Add(BuildTable(table));
                continue;
            }

            // List (unordered - * +, or ordered 1.), possibly nested by indent
            if (Regex.IsMatch(trimmed, @"^([-*+]|\d+\.)\s+"))
            {
                var block = new List<string>();
                while (i < lines.Length && (Regex.IsMatch(lines[i].TrimStart(), @"^([-*+]|\d+\.)\s+")
                                            || (lines[i].StartsWith("  ") && lines[i].Trim().Length > 0)))
                { block.Add(lines[i]); i++; }
                doc.Blocks.Add(BuildList(block, ordered: Regex.IsMatch(block[0].TrimStart(), @"^\d+\.\s+")));
                continue;
            }

            // Paragraph: gather consecutive plain lines
            var para = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                   && !IsBlockStart(lines[i]))
            { para.Add(lines[i].Trim()); i++; }
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(p.Inlines, string.Join(" ", para));
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static bool IsBlockStart(string line)
    {
        var t = line.TrimStart();
        return Regex.IsMatch(t, @"^#{1,6}\s") || t.StartsWith(">")
            || Regex.IsMatch(t, @"^([-*+]|\d+\.)\s+") || Regex.IsMatch(line.Trim(), @"^([-*_])\1{2,}$")
            || line.Contains('|');
    }

    private static Block Heading(int level, string text)
    {
        double size = level switch { 1 => 18, 2 => 15.5, 3 => 14, _ => 13 };
        var p = new Paragraph
        {
            FontSize = size, FontWeight = FontWeights.SemiBold,
            Foreground = B("TextPrimary"),
            Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 5),
        };
        AddInlines(p.Inlines, text);
        return p;
    }

    private static Block HorizontalRule() => new BlockUIContainer(new System.Windows.Controls.Border
    {
        Height = 1, Background = B("BorderStrong"), Margin = new Thickness(0, 6, 0, 10),
    })
    { Margin = new Thickness(0) };

    private static Block Blockquote(IEnumerable<string> lines)
    {
        var section = new Section
        {
            BorderBrush = B("Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2), Margin = new Thickness(0, 2, 0, 8),
        };
        var p = new Paragraph { Foreground = B("TextSecondary"), Margin = new Thickness(0) };
        AddInlines(p.Inlines, string.Join(" ", lines));
        section.Blocks.Add(p);
        return section;
    }

    private static Block BuildList(List<string> raw, bool ordered)
    {
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(18, 0, 0, 0),
        };
        ListItem? current = null;
        List? nested = null;
        foreach (var line in raw)
        {
            var indented = line.StartsWith("  ") && Regex.IsMatch(line.TrimStart(), @"^([-*+]|\d+\.)\s+");
            var m = Regex.Match(line.TrimStart(), @"^([-*+]|\d+\.)\s+(.*)$");
            if (!m.Success) continue;
            var content = m.Groups[2].Value;
            var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            AddInlines(para.Inlines, content);

            if (indented && current != null)
            {
                nested ??= new List { MarkerStyle = TextMarkerStyle.Circle, Margin = new Thickness(0), Padding = new Thickness(16, 2, 0, 0) };
                nested.ListItems.Add(new ListItem(para));
                if (!current.Blocks.Contains(nested)) current.Blocks.Add(nested);
            }
            else
            {
                nested = null;
                current = new ListItem(para);
                list.ListItems.Add(current);
            }
        }
        return list;
    }

    private static Block BuildTable(List<string> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10) };
        // Drop the separator row (the |---|---| line).
        var data = rows.Where(r => !Regex.IsMatch(r.Trim(), @"^\|?\s*:?-{2,}")).ToList();
        int cols = data.Count == 0 ? 0 : SplitCells(data[0]).Count;
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        for (int r = 0; r < data.Count; r++)
        {
            var cells = SplitCells(data[r]);
            var row = new TableRow();
            bool header = r == 0;
            if (header) row.Background = B("PageBg");
            for (int c = 0; c < cols; c++)
            {
                var text = c < cells.Count ? cells[c] : "";
                var para = new Paragraph { Margin = new Thickness(0) };
                if (header) para.FontWeight = FontWeights.SemiBold;
                AddInlines(para.Inlines, text);
                row.Cells.Add(new TableCell(para)
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderBrush = B("BorderSoft"), BorderThickness = new Thickness(0, 0, 1, 1),
                });
            }
            group.Rows.Add(row);
        }
        return table;
    }

    private static List<string> SplitCells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        return t.Split('|').Select(s => s.Trim()).ToList();
    }

    // ── inline: bold / code / links / bare urls ─────────────────────────────────
    private static readonly Regex Inline = new(
        @"(?<bold>\*\*(?<b>.+?)\*\*)|(?<code>`(?<c>[^`]+)`)|(?<link>\[(?<t>[^\]]+)\]\((?<u>[^)]+)\))|(?<url>https?://[^\s)]+)",
        RegexOptions.Compiled);

    private static void AddInlines(InlineCollection sink, string text)
    {
        int pos = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > pos) sink.Add(new Run(text[pos..m.Index]));
            if (m.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, m.Groups["b"].Value);
                sink.Add(bold);
            }
            else if (m.Groups["code"].Success)
            {
                sink.Add(new Run(m.Groups["c"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Background = B("CardBg"), FontSize = 12.5,
                });
            }
            else if (m.Groups["link"].Success)
            {
                sink.Add(Link(m.Groups["t"].Value, m.Groups["u"].Value));
            }
            else if (m.Groups["url"].Success)
            {
                sink.Add(Link(m.Groups["url"].Value, m.Groups["url"].Value));
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) sink.Add(new Run(text[pos..]));
    }

    private static Inline Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { Foreground = B("Accent") };
        try { link.NavigateUri = new Uri(url); } catch { return new Run(text); }
        link.RequestNavigate += OnNavigate;
        return link;
    }

    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* ignore */ }
        e.Handled = true;
    }

    private static Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
