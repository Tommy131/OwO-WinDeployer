using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinDeploy.App.Services;
using WinDeploy.App.ViewModels;

namespace WinDeploy.App;

public partial class MainWindow : Window
{
    // Same Segoe MDL2 glyph as the sidebar title icon.
    private const char TitleGlyph = (char)0xE950;   // Segoe MDL2 grid glyph (sidebar title)

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        Icon = RenderGlyphIcon(TitleGlyph.ToString());   // 用作窗口/任务栏的原生图标，与侧边栏标题一致
        // Native title bar gets its handle only at SourceInitialized; theme it then.
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Closed += (_, _) => (DataContext as MainViewModel)?.Terminal.Dispose();
    }

    /// <summary>Render a Segoe MDL2 glyph into a window/taskbar icon in the accent colour, so the native
    /// title bar matches the in-app branding.</summary>
    private static ImageSource RenderGlyphIcon(string glyph, int size = 64)
    {
        var color = (Application.Current.TryFindResource("Accent") as SolidColorBrush)?.Color
                    ?? Color.FromRgb(0x3B, 0x82, 0xF6);
        var typeface = new Typeface(new FontFamily("Segoe MDL2 Assets"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size * 0.74, new SolidColorBrush(color), 1.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
