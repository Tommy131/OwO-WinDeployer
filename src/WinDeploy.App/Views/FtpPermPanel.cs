using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.Views;

/// <summary>A reusable block of per-command permission checkboxes (List / Download / Upload / …) with
/// quick 只读 / 完全 / 无 presets, shared by the user and group dialogs.</summary>
internal sealed class FtpPermPanel
{
    private readonly (FtpPerm Flag, string Text)[] _defs =
    {
        (FtpPerm.List,      "列目录"),
        (FtpPerm.Download,  "下载"),
        (FtpPerm.Upload,    "上传"),
        (FtpPerm.Append,    "续传 / 追加"),
        (FtpPerm.Delete,    "删除文件"),
        (FtpPerm.Rename,    "重命名"),
        (FtpPerm.CreateDir, "新建目录"),
        (FtpPerm.DeleteDir, "删除目录"),
    };
    private readonly List<(FtpPerm Flag, CheckBox Box)> _boxes = new();
    private readonly List<Button> _presets = new();

    public FrameworkElement Build()
    {
        var outer = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        outer.Children.Add(new TextBlock { Text = "权限", FontSize = 12, Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 4, 0, 4) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < _defs.Length; i++)
        {
            var (flag, text) = _defs[i];
            var cb = new CheckBox { Content = text, Margin = new Thickness(0, 3, 12, 3) };
            Grid.SetColumn(cb, i % 2);
            Grid.SetRow(cb, i / 2);
            if (grid.RowDefinitions.Count <= i / 2) grid.RowDefinitions.Add(new RowDefinition());
            grid.Children.Add(cb);
            _boxes.Add((flag, cb));
        }
        outer.Children.Add(grid);

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        presets.Children.Add(Preset("只读", FtpPerm.ReadOnly));
        presets.Children.Add(Preset("完全控制", FtpPerm.Full));
        presets.Children.Add(Preset("无", FtpPerm.None));
        outer.Children.Add(presets);
        return outer;
    }

    private Button Preset(string text, FtpPerm value)
    {
        var b = new Button { Content = text, MinWidth = 64, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style s) b.Style = s;
        b.Click += (_, _) => Set(value);
        _presets.Add(b);
        return b;
    }

    public void Set(FtpPerm p)
    {
        foreach (var (flag, box) in _boxes) box.IsChecked = p.HasFlag(flag);
    }

    public FtpPerm Get()
    {
        var p = FtpPerm.None;
        foreach (var (flag, box) in _boxes) if (box.IsChecked == true) p |= flag;
        return p;
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var (_, box) in _boxes) box.IsEnabled = enabled;
        foreach (var b in _presets) b.IsEnabled = enabled;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
