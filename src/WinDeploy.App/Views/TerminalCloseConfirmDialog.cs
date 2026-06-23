using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinDeploy.App.Views;

/// <summary>Themed confirmation shown before closing a terminal session — disconnecting the PTY discards its
/// session state (running processes, command history, current directory), which can't be recovered.</summary>
public sealed class TerminalCloseConfirmDialog : Window
{
    public TerminalCloseConfirmDialog(string sessionTitle)
    {
        Title = "关闭终端会话";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // ── warning banner ──────────────────────────────────────────────
        var banner = new Border
        {
            Background = Brush("WarnBg"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var bannerRow = new Grid();
        bannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var warnIcon = new TextBlock
        {
            Text = "",   // Segoe MDL2 Warning glyph
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = Brush("WarnFg"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
        };
        Grid.SetColumn(warnIcon, 0);
        bannerRow.Children.Add(warnIcon);

        var bannerText = new StackPanel();
        Grid.SetColumn(bannerText, 1);
        bannerText.Children.Add(new TextBlock
        {
            Text = $"确定关闭「{sessionTitle}」？",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("WarnFg"),
            TextWrapping = TextWrapping.Wrap,
        });
        bannerText.Children.Add(new TextBlock
        {
            Text = "断开连接后，该终端的会话状态（正在运行的进程、命令历史、当前目录等）将全部丢失，且无法恢复。",
            FontSize = 12,
            Foreground = Brush("WarnFg"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.88,
        });
        bannerRow.Children.Add(bannerText);
        banner.Child = bannerRow;
        root.Children.Add(banner);

        // ── buttons ─────────────────────────────────────────────────────
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("MiniButton") is Style cancelStyle) cancel.Style = cancelStyle;
        cancel.Click += (_, _) => DialogResult = false;

        var confirm = new Button { Content = "关闭会话", MinWidth = 96, Margin = new Thickness(10, 0, 0, 0), IsDefault = true };
        if (Application.Current.TryFindResource("DangerButton") is Style confirmStyle) confirm.Style = confirmStyle;
        confirm.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
