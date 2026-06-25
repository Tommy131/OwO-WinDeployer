using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Services.Ftp;
using WinDeploy.App.Services.Net;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Deploy;

/// <summary>Copy a chosen file/folder to another machine and (over SSH) optionally run a deploy command.
/// Transfer method is selectable: SSH/SCP (key auth) or FTP / FTPS (reusing <see cref="FtpClient"/>).
/// Self-contained themed window — no XAML/DataTemplate needed.</summary>
public sealed class RemoteDeployDialog : Window
{
    private readonly TextBox _source, _host, _port, _user, _dir, _cmd, _output;
    private readonly PasswordBox _pass;
    private readonly ComboBox _method;
    private readonly Button _test, _go;

    // 0 = SSH/SCP · 1 = FTP · 2 = FTPS
    private bool IsSsh => _method.SelectedIndex == 0;
    private string TlsMode => _method.SelectedIndex == 2 ? "explicit" : "none";

    public RemoteDeployDialog(string defaultSource)
    {
        Title = Localizer.T("remote.title");
        Width = 640;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("PageBg");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // intro
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // form
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // output

        root.Children.Add(At(new TextBlock
        {
            Text = Localizer.T("remote.subtitle"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Foreground = Brush("TextSecondary"), Margin = new Thickness(0, 0, 0, 12),
        }, 0));

        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var r = 0;
        _method = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
        _method.Items.Add(new ComboBoxItem { Content = Localizer.T("remote.method.ssh") });
        _method.Items.Add(new ComboBoxItem { Content = Localizer.T("remote.method.ftp") });
        _method.Items.Add(new ComboBoxItem { Content = Localizer.T("remote.method.ftps") });
        _method.SelectedIndex = 0;
        _method.SelectionChanged += (_, _) => OnMethodChanged();
        AddRow(form, r++, "remote.method", _method);

        _source = MakeBox(defaultSource);
        var srcRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var browseFile = MakeButton("remote.browseFile", "MiniButton");
        var browseDir = MakeButton("remote.browseFolder", "MiniButton");
        browseFile.Click += (_, _) => Pick(folder: false);
        browseDir.Click += (_, _) => Pick(folder: true);
        DockPanel.SetDock(browseDir, Dock.Right);
        DockPanel.SetDock(browseFile, Dock.Right);
        srcRow.Children.Add(browseDir);
        srcRow.Children.Add(browseFile);
        _source.Margin = new Thickness(0, 0, 8, 0);
        srcRow.Children.Add(_source);
        AddRow(form, r++, "remote.source", srcRow);

        _host = MakeBox("");
        AddRow(form, r++, "remote.host", _host);
        _port = MakeBox("22");
        AddRow(form, r++, "remote.port", _port);
        _user = MakeBox(Environment.UserName);
        AddRow(form, r++, "remote.user", _user);
        _pass = new PasswordBox
        {
            Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(7, 5, 7, 5), FontSize = 13,
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        AddRow(form, r++, "remote.password", _pass);
        _dir = MakeBox("owo-win-deployer");
        AddRow(form, r++, "remote.dir", _dir);
        _cmd = MakeBox("windeploy apply --silent");
        AddRow(form, r++, "remote.command", _cmd);
        root.Children.Add(At(form, 1));

        _test = MakeButton("remote.test", "MiniButton");
        _go = MakeButton("remote.transfer", "PrimaryButton");
        var close = MakeButton("common.close", "MiniButton");
        _test.Click += async (_, _) => await RunAsync(test: true);
        _go.Click += async (_, _) => await RunAsync(test: false);
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 10) };
        buttons.Children.Add(_test);
        buttons.Children.Add(_go);
        buttons.Children.Add(close);
        root.Children.Add(At(buttons, 2));

        _output = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        root.Children.Add(At(_output, 3));

        Content = root;
        OnMethodChanged();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    /// <summary>SSH uses key auth (no password) and can run a command; FTP/FTPS use a password and can't.</summary>
    private void OnMethodChanged()
    {
        var ssh = IsSsh;
        _pass.IsEnabled = !ssh;
        _cmd.IsEnabled = ssh;
        // Switch the port to the new scheme's default only if it still holds the other scheme's default.
        var port = _port.Text.Trim();
        if (ssh && port == "21") _port.Text = "22";
        else if (!ssh && port == "22") _port.Text = "21";
    }

    private void Pick(bool folder)
    {
        if (folder)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = Localizer.T("remote.browseFolder") };
            if (d.ShowDialog() == true) _source.Text = d.FolderName;
        }
        else
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = Localizer.T("remote.browseFile"), CheckFileExists = true };
            if (d.ShowDialog() == true) _source.Text = d.FileName;
        }
    }

    private async Task RunAsync(bool test)
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) { _output.Text = Localizer.T("remote.needHost"); return; }
        var source = _source.Text.Trim();
        if (!test && source.Length == 0) { _output.Text = Localizer.T("remote.needSource"); return; }
        if (!int.TryParse(_port.Text.Trim(), out var port) || port <= 0) port = IsSsh ? 22 : 21;
        var user = _user.Text.Trim().Length == 0 ? Environment.UserName : _user.Text.Trim();

        _test.IsEnabled = _go.IsEnabled = false;
        _output.Text = Localizer.T(test ? "remote.testing" : "remote.deploying");
        try
        {
            bool ok;
            string output;
            if (IsSsh)
            {
                (ok, output) = test
                    ? await RemoteDeploy.TestAsync(host, user, port)
                    : await RemoteDeploy.DeployAsync(host, user, port, source, _dir.Text.Trim(), _cmd.Text.Trim());
            }
            else
            {
                (ok, output) = test
                    ? await FtpTransfer.TestAsync(host, port, TlsMode, user, _pass.Password)
                    : await FtpTransfer.UploadAsync(host, port, TlsMode, user, _pass.Password, source, _dir.Text.Trim());
            }

            if (!test) AuditLog.Action($"远程部署（{(IsSsh ? "ssh" : TlsMode == "explicit" ? "ftps" : "ftp")}）{user}@{host}:{port} · {(ok ? "成功" : "失败")}");
            var tag = (ok ? "✓ " : "✗ ") + Localizer.T(test ? (ok ? "remote.testOk" : "remote.testFail") : (ok ? "remote.done" : "remote.fail"));
            _output.Text = test ? tag + "\n\n" + output : output + "\n\n" + tag;
        }
        catch (Exception ex) { _output.Text = Localizer.Format("remote.error", ex.Message); }
        finally { _test.IsEnabled = _go.IsEnabled = true; }
    }

    // ── tiny themed-control helpers ─────────────────────────────────────────────
    private void AddRow(Grid form, int row, string labelKey, FrameworkElement control)
    {
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = Localizer.T(labelKey), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 8, 5) };
        Grid.SetRow(label, row); Grid.SetColumn(label, 0);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        form.Children.Add(label);
        form.Children.Add(control);
    }

    private TextBox MakeBox(string initial) => new()
    {
        Text = initial, FontSize = 13, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 4, 0, 4),
        Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
    };

    private Button MakeButton(string key, string styleKey)
    {
        var b = new Button { Content = Localizer.T(key), MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        if (Application.Current.TryFindResource(styleKey) is Style s) b.Style = s;
        return b;
    }

    private static FrameworkElement At(FrameworkElement el, int row) { Grid.SetRow(el, row); return el; }
    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
