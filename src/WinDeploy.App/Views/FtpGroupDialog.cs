using System.Windows;
using System.Windows.Controls;
using WinDeploy.App.Services.Ftp;

namespace WinDeploy.App.Views;

/// <summary>Add / edit a user group: name, an optional default home for members, a description, and the
/// permission set members inherit when they opt in.</summary>
public sealed class FtpGroupDialog : Window
{
    private readonly TextBox _name;
    private readonly TextBox _home;
    private readonly TextBox _desc;
    private readonly FtpPermPanel _perms = new();

    public FtpGroup? Result { get; private set; }

    public FtpGroupDialog(FtpGroup? existing)
    {
        Title = existing == null ? "添加分组" : $"编辑分组 · {existing.Name}";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = FtpUserDialog.Brush("PageBg");

        _name = FtpUserDialog.Tb(existing?.Name ?? "");
        _home = FtpUserDialog.Tb(existing?.Home ?? "");
        _desc = FtpUserDialog.Tb(existing?.Description ?? "");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(FtpUserDialog.Label("分组名称"));
        root.Children.Add(_name);
        root.Children.Add(FtpUserDialog.Label("默认主目录（可选；成员未单独指定主目录时使用）"));
        var homeRow = new DockPanel();
        var browse = new Button { Content = "浏览…", MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style ms) browse.Style = ms;
        browse.Click += (_, _) => { var d = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认主目录" }; if (d.ShowDialog() == true) _home.Text = d.FolderName; };
        DockPanel.SetDock(browse, Dock.Right);
        homeRow.Children.Add(browse);
        homeRow.Children.Add(_home);
        root.Children.Add(homeRow);
        root.Children.Add(FtpUserDialog.Label("描述（可选）"));
        root.Children.Add(_desc);

        root.Children.Add(_perms.Build());
        _perms.Set(existing?.Permissions ?? FtpPerm.ReadOnly);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = existing == null ? "添加" : "保存", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => OnOk();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        SourceInitialized += (_, _) => Services.ThemeManager.ApplyTitleBar(this);
    }

    private void OnOk()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { MessageBox.Show("请填写分组名称", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Result = new FtpGroup
        {
            Name = name,
            Home = _home.Text.Trim().Length == 0 ? null : _home.Text.Trim(),
            Description = _desc.Text.Trim().Length == 0 ? null : _desc.Text.Trim(),
            Permissions = _perms.Get(),
        };
        DialogResult = true;
    }
}
