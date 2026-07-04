// ============================================================================
//  OwO! Win Deployer — Community Edition
//  Copyright (C) 2026 HanskiJay (GitHub: Tommy131) <hanskijay@owoblog.com>
// ============================================================================
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OwOWinDeployer.Core.I18n;

namespace OwOWinDeployer.App.Views.Common;

/// <summary>Shown once after the app updates (or on first run): welcomes the user, re-states that this is the free
/// open-source <b>Community Edition</b> for personal use only, and surfaces the author's contact / support
/// channels. Purely informational — a single OK closes it; the caller records the version so it won't repeat.</summary>
public sealed class WelcomeDialog : Window
{
    public WelcomeDialog(bool firstRun)
    {
        Title = Localizer.T("welcome.title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = B("PageBg");

        var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

        // App name + edition badge
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock { Text = "🎉", FontSize = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        titleRow.Children.Add(new TextBlock
        {
            Text = firstRun ? Localizer.Format("welcome.firstRun", AppInfo.Name)
                            : Localizer.Format("welcome.updated", AppInfo.Name, AppInfo.Version),
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B("TextPrimary"), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(titleRow);

        var edition = new Border
        {
            Background = B("AccentBg"), BorderBrush = B("Accent"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
            Child = new TextBlock { Text = Localizer.T("about.edition"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = B("Accent") },
        };
        root.Children.Add(edition);

        // Usage notice (community / personal use only / no commercial)
        var notice = new Border
        {
            Background = B("WarnBg"), BorderBrush = B("WarnFg"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = Localizer.T("welcome.notice"), FontSize = 12.5, Foreground = B("TextPrimary"),
                TextWrapping = TextWrapping.Wrap, LineHeight = 19,
            },
        };
        root.Children.Add(notice);

        // Contact / support channels
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("welcome.contact.title"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = B("TextSecondary"), Margin = new Thickness(0, 16, 0, 6),
        });
        root.Children.Add(ContactRow(Localizer.T("settings.aboutDev.github"), AppInfo.AuthorGitHubUrl));
        root.Children.Add(ContactRow(Localizer.T("settings.aboutDev.email"), $"mailto:{AppInfo.Email}", AppInfo.Email));
        root.Children.Add(ContactRow(Localizer.T("settings.aboutDev.donation"), AppInfo.DonationUrl));
        root.Children.Add(ContactRow(Localizer.T("settings.aboutDev.service"), AppInfo.ServiceUrl));
        root.Children.Add(ContactRow(Localizer.T("settings.aboutDev.blog"), AppInfo.BlogUrl));

        // Buttons
        var bar = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var donate = new Button { Content = Localizer.T("welcome.btn.donate"), HorizontalAlignment = HorizontalAlignment.Left };
        if (Application.Current.TryFindResource("MiniButton") is Style ms) donate.Style = ms;
        donate.Click += (_, _) => OpenUrl(AppInfo.DonationUrl);
        Grid.SetColumn(donate, 0);
        bar.Children.Add(donate);

        var ok = new Button { Content = Localizer.T("welcome.btn.ok"), MinWidth = 110, IsDefault = true, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style ps) ok.Style = ps;
        ok.Click += (_, _) => { DialogResult = true; };
        Grid.SetColumn(ok, 1);
        bar.Children.Add(ok);
        root.Children.Add(bar);

        Content = root;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static UIElement ContactRow(string label, string url, string? shownText = null)
    {
        var tb = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        tb.Inlines.Add(new Run(label) { Foreground = B("TextSecondary") });
        var link = new Hyperlink(new Run(shownText ?? url)) { Foreground = B("Accent") };
        try { link.NavigateUri = new Uri(url); } catch { /* keep as text */ }
        link.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        tb.Inlines.Add(link);
        return tb;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
