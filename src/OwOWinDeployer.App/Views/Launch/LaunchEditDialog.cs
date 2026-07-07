using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OwOWinDeployer.App.Services.Launch;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.Views.Launch;

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
    private readonly CheckBox _keepOpen;
    private readonly TextBlock _targetLabel;
    private readonly Button _targetBrowse;
    private readonly TextBlock _argsLabel;
    private readonly TextBlock _workLabel;
    private readonly UIElement _workRow;

    /// <summary>The edited item, populated when the dialog is accepted; null while cancelled.</summary>
    public LaunchItem? Result { get; private set; }

    /// <summary>Strict web-address shape for the URL kind: an optional http/https scheme, then a host that is a
    /// dotted domain (label.label.tld), <c>localhost</c>, or an IPv4 address, then an optional :port and path/
    /// query/fragment (no whitespace). Matches how <see cref="LaunchRunner"/> later prepends https:// to a bare host.</summary>
    private static readonly Regex UrlPattern = new(
        @"^(?:https?://)?(?:localhost|(?:\d{1,3}\.){3}\d{1,3}|(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,})(?::\d{1,5})?(?:[/?#]\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        // Target + Browse (the label + the file picker both adapt to the kind in UpdateHints — for a
        // command the "target" is the command line, not a file, so the picker is hidden there).
        _targetLabel = Label("launcher.edit.target");
        root.Children.Add(_targetLabel);
        _target = Field(_item.Target, out var targetRow, out _targetBrowse, browseKey: "launcher.edit.browse", onBrowse: BrowseTarget);
        root.Children.Add(targetRow);
        _targetHint = Hint();
        root.Children.Add(_targetHint);

        // Arguments (hidden for URLs — a website takes no arguments)
        _argsLabel = Label("launcher.edit.args");
        root.Children.Add(_argsLabel);
        _args = Field(_item.Args ?? "");
        root.Children.Add(_args);

        // Working directory + Browse (only meaningful for a command — an app is opened via ShellExecute
        // and a URL has no working directory, so the picker is hidden for both)
        _workLabel = Label("launcher.edit.workDir");
        root.Children.Add(_workLabel);
        _workDir = Field(_item.WorkingDir ?? "", out _workRow, out _, browseKey: "launcher.edit.browse", onBrowse: BrowseWorkDir);
        root.Children.Add(_workRow);

        // Keep the console window open (Command kind only — visibility toggled in UpdateHints)
        _keepOpen = new CheckBox
        {
            Content = Localizer.T("launcher.edit.keepOpen"), IsChecked = _item.KeepOpen,
            Foreground = Brush("TextPrimary"), Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_keepOpen);

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
    {
        var kind = SelectedKind;

        // The "target" means different things per kind — relabel it so it's unambiguous.
        _targetLabel.Text = Localizer.T(kind switch
        {
            LaunchKind.Url => "launcher.edit.target.url",
            LaunchKind.Command => "launcher.edit.target.command",
            _ => "launcher.edit.target.app",
        });
        _targetHint.Text = kind switch
        {
            LaunchKind.Url => Localizer.T("launcher.edit.hint.url"),
            LaunchKind.Command => Localizer.T("launcher.edit.hint.command"),
            _ => Localizer.T("launcher.edit.hint.app"),
        };

        // The file picker only makes sense when the target is a file/app — a URL or command is typed.
        // For an app this is the single selector the user needs, so the working-directory picker is hidden.
        _targetBrowse.Visibility = kind == LaunchKind.App ? Visibility.Visible : Visibility.Collapsed;

        // Arguments are hidden for a website (a URL takes none).
        var showArgs = kind != LaunchKind.Url ? Visibility.Visible : Visibility.Collapsed;
        _argsLabel.Visibility = showArgs;
        _args.Visibility = showArgs;

        // Working directory only applies to a command (an app opens via ShellExecute, a URL has none).
        var showWorkDir = kind == LaunchKind.Command ? Visibility.Visible : Visibility.Collapsed;
        _workLabel.Visibility = showWorkDir;
        _workRow.Visibility = showWorkDir;

        // "Keep window open" only applies to a shell command.
        _keepOpen.Visibility = kind == LaunchKind.Command ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseTarget()
    {
        var dlg = new OpenFileDialog { Title = Localizer.T("launcher.edit.target.app"), CheckFileExists = false };
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
        // A website must look like a real address — reject anything that isn't a valid URL / host.
        if (SelectedKind == LaunchKind.Url && !UrlPattern.IsMatch(target))
        {
            Dialogs.Show(Localizer.T("launcher.edit.badUrl"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _target.Focus();
            _target.SelectAll();
            return;
        }
        _item.Title = _title.Text.Trim();
        _item.Kind = SelectedKind;
        _item.Target = target;
        // Only persist fields the kind actually uses, so a hidden box's stale text never leaks onto the item.
        _item.Args = SelectedKind == LaunchKind.Url ? null : Blank(_args.Text);
        _item.WorkingDir = SelectedKind == LaunchKind.Command ? Blank(_workDir.Text) : null;
        _item.RunAsAdmin = _admin.IsChecked == true;
        _item.KeepOpen = SelectedKind == LaunchKind.Command && _keepOpen.IsChecked == true;
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

    /// <summary>A text field with a trailing Browse button, returned via <paramref name="row"/>; the button
    /// itself comes back in <paramref name="browse"/> so callers can show/hide it per kind.</summary>
    private static TextBox Field(string initial, out UIElement row, out Button browse, string browseKey, Action onBrowse)
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
        browse = btn;
        return box;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
