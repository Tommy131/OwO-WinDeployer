using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinDeploy.App.Views;

/// <summary>Themed help dialog listing the exact Cloudflare permissions a scoped API token needs for DDNS.
/// Built in code using the app's theme resource brushes (resolved at open time), so it matches the current
/// light / dark theme automatically — same approach as <see cref="DevModeConfirmDialog"/>.</summary>
public sealed class CloudflarePermsDialog : Window
{
    public CloudflarePermsDialog()
    {
        Title = "Cloudflare API 令牌所需权限";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        root.Children.Add(new TextBlock
        {
            Text = "创建用于 DDNS 的 API 令牌时，请按下表申请权限：",
            FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimary"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "Cloudflare 控制台 → 个人资料 → API 令牌 → 创建令牌 → 使用「自定义令牌」模板",
            FontSize = 12, Foreground = Brush("TextTertiary"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── 权限 ──────────────────────────────────────────────────────────────
        root.Children.Add(SectionTitle("权限 (Permissions)"));
        root.Children.Add(PermRow(new[] { "区域 Zone", "区域 Zone", "读取 Read" },
            "列出账号下的全部域名（Zone）。"));
        root.Children.Add(PermRow(new[] { "区域 Zone", "DNS", "编辑 Edit" },
            "读取并新建 / 修改 DNS 解析记录 —— DDNS 的核心权限。"));

        // ── 区域资源 ──────────────────────────────────────────────────────────
        root.Children.Add(SectionTitle("区域资源 (Zone Resources)"));
        root.Children.Add(PermRow(new[] { "包含 Include", "所有区域 All zones" },
            "或仅选择你要用于 DDNS 的特定域名。"));

        // ── 说明 ──────────────────────────────────────────────────────────────
        var note = new Border
        {
            Background = Brush("AccentBg"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 16, 0, 0),
        };
        note.Child = new TextBlock
        {
            Text = "说明：以上为最小权限集，只需 Zone 范围，无需 User / Account 权限。正因如此，Cloudflare 的「令牌验证」接口可能返回 #1000，但不影响 DDNS —— 本工具以「能否读取域名」作为有效判据。其余项（客户端 IP 地址筛选、TTL）留空即可。",
            FontSize = 12, Foreground = Brush("Accent"), TextWrapping = TextWrapping.Wrap, LineHeight = 19,
        };
        root.Children.Add(note);

        // ── 按钮 ──────────────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var go = new Button { Content = "前往创建令牌", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("PrimaryButton") is Style gs) go.Style = gs;
        go.Click += (_, _) => Open("https://dash.cloudflare.com/profile/api-tokens");

        var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true, IsDefault = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cs) close.Style = cs;
        close.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(go);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
        Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 8, 0, 6),
    };

    /// <summary>One permission line: a chain of chips (joined by ›) above a one-line description.</summary>
    private static UIElement PermRow(string[] chips, string desc)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < chips.Length; i++)
        {
            if (i > 0)
                chipRow.Children.Add(new TextBlock
                {
                    Text = "›", FontSize = 14, Foreground = Brush("TextTertiary"),
                    Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                });
            chipRow.Children.Add(Chip(chips[i]));
        }
        panel.Children.Add(chipRow);

        panel.Children.Add(new TextBlock
        {
            Text = desc, FontSize = 12, Foreground = Brush("TextTertiary"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 5, 0, 0),
        });
        return panel;
    }

    private static Border Chip(string text) => new()
    {
        Background = Brush("CardBg"), BorderBrush = Brush("Accent"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 3, 9, 3),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Brush("Accent"), VerticalAlignment = VerticalAlignment.Center },
    };

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
