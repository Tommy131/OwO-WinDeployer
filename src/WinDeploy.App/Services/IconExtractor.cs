using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Services;

/// <summary>Extracts an application's real icon from its .exe (so installed apps show their actual icon
/// instead of a bundled favicon — works on any machine, no network).</summary>
public static class IconExtractor
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static BitmapSource? FromExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        var large = new IntPtr[1];
        try
        {
            var n = ExtractIconEx(exe, 0, large, new IntPtr[1], 1);
            if (n == 0 || large[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }
}
