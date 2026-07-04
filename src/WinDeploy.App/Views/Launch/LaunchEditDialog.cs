using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinDeploy.App.Services.Launch;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Launch;

/// <summary>Themed add/edit form for a <see cref="LaunchItem"/> (built in code, like the other Common dialogs).
/// On OK, <see cref="Result"/> holds the edited item — the same instance when editing, a new one when adding.</summary>
public sealed class LaunchEditDialog : Window
{
    private readonly LaunchItem _item;
    private readonly TextBox _title;
    private readonly ComboBox _kind;
    private readonly TextBox _target;
    private readonly TextBox _args;
    private readonly TextBox _workDir;
    private readonly CheckBox _admin;

    /// <summary>The edited item, populated when the dialog is accepted; null while cancelled.</summary>
    public LaunchItem? Result { get; private set; }

    public LaunchEditDialog(LaunchItem? existing)
    {
        // Edit a detached working copy (carrying Id/Done); the caller commits only if it's not a duplicate.
        _item = existing?.Clone() ?? new LaunchItem();
        Title = Localizer.T(existing == null ? "launcher.edit.addTitle" : "launcher.edit.editTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(18) };

        // Title
        root.Children.Add(Label("launcher.edit.name"));
        _title = Field(_item.Title);
        root.Children.Add(_title);

        // Kind
        root.Children.Add(Label("launcher.edit.kind"));
        _kind = new ComboBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        foreach (var (kind, key) in new[]
                 {
                     (LaunchKind.App, "launcher.kind.app"),
                     (LaunchKind.Url, "launcher.kind.url"),
                     (LaunchKind.Command, "launcher.kind.command"),
                 })
            _kind.Items.Add(new ComboBoxItem { Content = Localizer.T(key), Tag = kind });
        _kind.SelectedIndex = (int)_item.Kind;
        _kind.SelectionChanged += (_, _) => UpdateHints();
        root.Children.Add(_kind);

        // Target + Browse (browse only meaningful for App/file)
        root.Children.Add(Label("launcher.edit.target"));
        _target = Field(_item.Target, out var targetRow, browseKey: "launcher.edit.browse", onBrowse: BrowseTarget);
        root.Children.Add(targetRow);
        _targetHint = Hint();
        root.Children.Add(_targetHint);

        // Arguments
        root.Children.Add(Label("launcher.edit.args"));
        _args = Field(_item.Args ?? "");
        root.Children.Add(_args);

        // Working directory + Browse
        root.Children.Add(Label("launcher.edit.workDir"));
        _workDir = Field(_item.WorkingDir ?? "", out var workRow, browseKey: "launcher.edit.browse", onBrowse: BrowseWorkDir);
        root.Children.Add(workRow);

        // Run as admin
        _admin = new CheckBox
        {
            Content = Localizer.T("launcher.edit.admin"), IsChecked = _item.RunAsAdmin,
            Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_admin);

        // Buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Localizer.T("common.ok"), MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        UpdateHints();
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private readonly TextBlock _targetHint;

    private LaunchKind SelectedKind => (LaunchKind)(((ComboBoxItem)_kind.SelectedItem).Tag);

    private void UpdateHints()
        => _targetHint.Text = SelectedKind switch
        {
            LaunchKind.Url => Localizer.T("launcher.edit.hint.url"),
            LaunchKind.Command => Localizer.T("launcher.edit.hint.command"),
            _ => Localizer.T("launcher.edit.hint.app"),
        };

    private void BrowseTarget()
    {
        var dlg = new OpenFileDialog { Title = Localizer.T("launcher.edit.target"), CheckFileExists = false };
        if (dlg.ShowDialog(this) == true)
        {
            _target.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(_title.Text))
                _title.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void BrowseWorkDir()
    {
        var dlg = new OpenFolderDialog { Title = Localizer.T("launcher.edit.workDir") };
        if (dlg.ShowDialog(this) == true) _workDir.Text = dlg.FolderName;
    }

    private void Accept()
    {
        var target = _target.Text.Trim();
        if (target.Length == 0)
        {
            Dialogs.Show(Localizer.T("launcher.edit.needTarget"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _item.Title = _title.Text.Trim();
        _item.Kind = SelectedKind;
        _item.Target = target;
        _item.Args = Blank(_args.Text);
        _item.WorkingDir = Blank(_workDir.Text);
        _item.RunAsAdmin = _admin.IsChecked == true;
        Result = _item;
        DialogResult = true;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── tiny themed control builders ─────────────────────────────────────────────
    private static TextBlock Label(string key) => new()
    {
        Text = Localizer.T(key), FontSize = 12, Foreground = Brush("TextSecondary"),
        Margin = new Thickness(0, 0, 0, 3),
    };

    private static TextBlock Hint() => new()
    {
        FontSize = 11, Foreground = Brush("TextTertiary"), TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, -6, 0, 10),
    };

    private static TextBox Field(string initial) => new()
    {
        Text = initial, FontSize = 13, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 10),
        Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
    };

    /// <summary>A text field with a trailing Browse button, returned via <paramref name="row"/>.</summary>
    private static TextBox Field(string initial, out UIElement row, string browseKey, Action onBrowse)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new TextBox
        {
            Text = initial, FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        Grid.SetColumn(box, 0);
        var btn = new Button { Content = Localizer.T(browseKey), MinWidth = 72, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.TryFindResource("MiniButton") is Style s) btn.Style = s;
        btn.Click += (_, _) => onBrowse();
        Grid.SetColumn(btn, 1);
        grid.Children.Add(box);
        grid.Children.Add(btn);
        row = grid;
        return box;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
