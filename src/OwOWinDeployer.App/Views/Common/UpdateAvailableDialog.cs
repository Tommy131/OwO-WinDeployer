using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OwOWinDeployer.App.Services.Software;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.Views.Common;

/// <summary>What the user chose in the update dialog.</summary>
public enum UpdateChoice { Later, UpdateNow, Ignore }

/// <summary>The rich "a new version is available" dialog: shows the version transition, a prominent warning when
/// the offered build is a pre-release, and the GitHub release notes rendered as formatted Markdown (scrollable).
/// Three actions: update now, ignore this version (don't nag again until a newer one ships), or later.</summary>
public sealed class UpdateAvailableDialog : Window
{
    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdateAvailableDialog(UpdateCheck check)
    {
        Title = Localizer.T("update.dialog.title");
        Width = 640;
        Height = 600;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = B("PageBg");

        var grid = new Grid { Margin = new Thickness(20, 18, 20, 16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // prerelease banner
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // notes label
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // notes
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        // ── header: title + version transition ──
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = Localizer.Format("update.dialog.heading", check.Latest),
            FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new TextBlock
        {
            Text = Localizer.Format("update.dialog.versionLine", check.Current, check.Latest),
            FontSize = 12.5, Foreground = B("TextSecondary"), Margin = new Thickness(0, 3, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(check.ReleaseName) && check.ReleaseName != check.Tag)
            header.Children.Add(new TextBlock
            {
                Text = check.ReleaseName, FontSize = 12, Foreground = B("TextTertiary"),
                Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // ── pre-release warning banner ──
        if (check.IsPrerelease)
        {
            var banner = new Border
            {
                Background = B("WarnBg"), BorderBrush = B("WarnFg"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var brow = new StackPanel { Orientation = Orientation.Horizontal };
            brow.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                Foreground = B("WarnFg"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 10, 0),
            });
            var btext = new StackPanel();
            btext.Children.Add(new TextBlock
            {
                Text = Localizer.T("update.prerelease.badge"), FontWeight = FontWeights.SemiBold,
                FontSize = 13, Foreground = B("WarnFg"),
            });
            btext.Children.Add(new TextBlock
            {
                Text = Localizer.T("update.prerelease.warn"), FontSize = 12.5, Foreground = B("TextPrimary"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
            });
            brow.Children.Add(btext);
            banner.Child = brow;
            Grid.SetRow(banner, 1);
            grid.Children.Add(banner);
        }

        // ── notes label ──
        var notesLabel = new TextBlock
        {
            Text = Localizer.T("update.dialog.notes"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = B("TextSecondary"), Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(notesLabel, 2);
        grid.Children.Add(notesLabel);

        // ── notes (rendered Markdown, scrollable) ──
        var notesBorder = new Border
        {
            Background = B("CardBg"), BorderBrush = B("BorderSoft"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 8, 10),
        };
        var hasNotes = !string.IsNullOrWhiteSpace(check.Notes);
        var viewer = new FlowDocumentScrollViewer
        {
            Document = MarkdownRenderer.ToFlowDocument(
                hasNotes ? check.Notes : Localizer.T("update.dialog.noNotes")),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = B("TextPrimary"),
        };
        notesBorder.Child = viewer;
        Grid.SetRow(notesBorder, 3);
        grid.Children.Add(notesBorder);

        // ── buttons ──
        var bar = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pageLink = new Button
        {
            Content = Localizer.T("update.btn.releasePage"), HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (Application.Current.TryFindResource("MiniButton") is Style ms) pageLink.Style = ms;
        pageLink.Click += (_, _) => OpenUrl(check.HtmlUrl);
        Grid.SetColumn(pageLink, 0);
        bar.Children.Add(pageLink);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ignore = new Button { Content = Localizer.T("update.btn.ignore"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        var later = new Button { Content = Localizer.T("update.btn.later"), MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var update = new Button { Content = Localizer.T("update.btn.updateNow"), MinWidth = 110, IsDefault = true };
        if (Application.Current.TryFindResource("MiniButton") is Style m1) ignore.Style = m1;
        if (Application.Current.TryFindResource("MiniButton") is Style m2) later.Style = m2;
        if (Application.Current.TryFindResource("PrimaryButton") is Style ps) update.Style = ps;
        ignore.Click += (_, _) => { Choice = UpdateChoice.Ignore; DialogResult = true; };
        later.Click += (_, _) => { Choice = UpdateChoice.Later; DialogResult = false; };
        update.Click += (_, _) => { Choice = UpdateChoice.UpdateNow; DialogResult = true; };
        right.Children.Add(ignore);
        right.Children.Add(later);
        right.Children.Add(update);
        Grid.SetColumn(right, 1);
        bar.Children.Add(right);

        Grid.SetRow(bar, 4);
        grid.Children.Add(bar);

        Content = grid;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
