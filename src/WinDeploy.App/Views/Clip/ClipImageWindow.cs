using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Views.Clip;

/// <summary>A resizable viewer that pops a board image up at a large, fit-to-window size. The image scales
/// with the window (Uniform), so it always stays fully visible; Esc or the title-bar close button dismiss it.</summary>
public sealed class ClipImageWindow : Window
{
    public ClipImageWindow(BitmapSource image, string title)
    {
        Title = title;
        Background = Brush("PageBg");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // Open at the image's size, capped to 90% of the work area; scales with the window thereafter.
        var area = SystemParameters.WorkArea;
        var scale = Math.Min(1.0, Math.Min(area.Width * 0.9 / image.PixelWidth, area.Height * 0.9 / image.PixelHeight));
        Width = Math.Max(320, image.PixelWidth * scale + 16);
        Height = Math.Max(240, image.PixelHeight * scale + 40);

        Content = new Image { Source = image, Stretch = Stretch.Uniform, Margin = new Thickness(8) };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
    }

    private static Brush Brush(string key) => Application.Current.TryFindResource(key) as Brush ?? Brushes.Black;
}
