using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Services.Infra;

/// <summary>Pure-WPF "frosted glass": grabs the screen region behind a window so it can be blurred (WPF
/// <c>BlurEffect</c>) and clipped to smooth rounded corners — the only way to get BOTH a real see-through blur
/// AND anti-aliased rounded corners, since a per-pixel-transparent (AllowsTransparency) window can't be rounded
/// smoothly by DWM and the DWM acrylic backdrop renders flat on some machines.
///
/// <see cref="ExcludeFromCapture"/> marks the widget invisible to screen capture (Win10 2004+), so the grab sees
/// what's behind it instead of itself — no hide/show flicker, no feedback loop.</summary>
public static class ScreenBlur
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Make the window invisible to screen capture, so <see cref="Capture"/> grabs the content behind it.</summary>
    public static void ExcludeFromCapture(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE);
        }
        catch { /* pre-2004 Windows — capture will simply include the widget; still usable */ }
    }

    /// <summary>Grab a screen rectangle (physical pixels) as a frozen <see cref="BitmapSource"/>, or null on failure.</summary>
    public static BitmapSource? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);

            var hbmp = bmp.GetHbitmap();
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
    }
}
