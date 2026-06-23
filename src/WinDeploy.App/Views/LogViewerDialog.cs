using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services;

namespace WinDeploy.App.Views;

/// <summary>Read-only tail viewer for a server log file, with refresh / clear / open-folder.</summary>
public sealed class LogViewerDialog : Window
{
    private readonly string _path;
    private readonly TextBox _text;

    public LogViewerDialog(string title, string path)
    {
        _path = path;
        Title = $"{title} · 日志";
        Width = 860;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("PageBg");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pathTb = new TextBlock { Text = path, FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Foreground = Brush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = path };
        Grid.SetRow(pathTb, 0);
        root.Children.Add(pathTb);

        _text = new TextBox
        {
            IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderSoft"),
            Padding = new Thickness(8),
        };
        Grid.SetRow(_text, 1);
        root.Children.Add(_text);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(MakeBtn("↻ 刷新", "MiniButton", (_, _) => Reload()));
        bar.Children.Add(MakeBtn("打开所在文件夹", "MiniButton", (_, _) => OpenFolder()));
        bar.Children.Add(MakeBtn("清空日志", "DangerButton", (_, _) => Clear()));
        var close = MakeBtn("关闭", "MiniButton", (_, _) => Close());
        close.IsCancel = true;
        bar.Children.Add(close);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        Content = root;
        Loaded += (_, _) => Reload();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private Button MakeBtn(string text, string style, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        if (Application.Current.TryFindResource(style) is Style s) b.Style = s;
        b.Click += onClick;
        return b;
    }

    private void Reload()
    {
        _text.Text = ServerManager.ReadTail(_path);
        _text.ScrollToEnd();
        _text.CaretIndex = _text.Text.Length;
    }

    private void OpenFolder()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (dir != null && System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void Clear()
    {
        if (MessageBox.Show($"确定清空日志文件？\n{_path}\n\n（不可恢复）", "清空日志", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var (ok, msg) = ServerManager.ClearLog(_path);
        if (!ok) MessageBox.Show(msg, "清空日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        Reload();
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
