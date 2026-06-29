using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDeploy.App.Behaviors;
using WinDeploy.Core.I18n;

namespace WinDeploy.App.Views.Clip;

/// <summary>Shown to the joiner on an inbound pairing request: enter the 6-digit PIN the initiator is
/// reading off their screen. On a wrong PIN the manager re-invokes this with <paramref name="attempt"/> &gt; 0
/// so it shows a retry hint. Returns the entered PIN via <see cref="Pin"/>, or cancels pairing.</summary>
public sealed class ClipPinDialog : Window
{
    private readonly TextBox _box;
    public string Pin => _box.Text.Trim();

    public ClipPinDialog(string peerName, int attempt)
    {
        Title = Localizer.T("clip.pin.title");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brush("PageBg");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = Localizer.Format("clip.pin.prompt", string.IsNullOrWhiteSpace(peerName) ? "?" : peerName),
            TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Brush("TextPrimary"),
        });
        root.Children.Add(new TextBlock
        {
            Text = Localizer.T("clip.pin.hint"), TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = Brush("TextTertiary"), Margin = new Thickness(0, 6, 0, 12),
        });

        _box = new TextBox
        {
            FontSize = 26, MaxLength = 6, Padding = new Thickness(10, 8, 10, 8),
            TextAlignment = TextAlignment.Center, FontFamily = new FontFamily("Consolas"),
            Background = Brush("CardBg"), Foreground = Brush("TextPrimary"), BorderBrush = Brush("BorderStrong"),
        };
        InputFilter.SetMode(_box, "digits");   // PIN is digits only
        root.Children.Add(_box);

        if (attempt > 0)
            root.Children.Add(new TextBlock
            {
                Text = Localizer.T("clip.pin.retry"), FontSize = 12.5, Foreground = Brush("FailFg"),
                Margin = new Thickness(0, 8, 0, 0),
            });

        root.Children.Add(Buttons());
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private StackPanel Buttons()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = Localizer.T("clip.pin.join"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = Localizer.T("common.cancel"), MinWidth = 72, IsCancel = true };
        if (Application.Current.TryFindResource("PrimaryButton") is Style okS) ok.Style = okS;
        if (Application.Current.TryFindResource("MiniButton") is Style caS) cancel.Style = caS;
        ok.Click += (_, _) =>
        {
            if (Pin.Length != 6) { Dialogs.Show(Localizer.T("clip.pin.badLength"), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        return buttons;
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
