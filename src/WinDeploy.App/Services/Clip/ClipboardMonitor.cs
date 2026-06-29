using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinDeploy.App.Services.Clip;

/// <summary>Watches the local Windows clipboard via the modern format-listener API (event-driven, not
/// polling) using a hidden message-only window. On every change it captures the current text or image and
/// raises <see cref="Captured"/> on the UI thread. To avoid an echo loop when a remote entry is mirrored
/// onto the local clipboard, the manager calls <see cref="Suppress"/> with that entry's content hash so the
/// resulting change is ignored once.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private HwndSource? _source;
    private string? _suppressHash;     // skip the next change matching this (we caused it)
    private string? _lastHash;         // dedupe identical consecutive copies

    /// <summary>Raised on the UI thread with a freshly captured local clipboard entry (origin unset).</summary>
    public event Action<ClipEntry>? Captured;
    public event Action<string>? Log;

    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;
    public bool Running { get; private set; }

    /// <summary>Begin listening. MUST be called on the UI (STA) thread so clipboard reads are valid.</summary>
    public void Start()
    {
        if (Running) return;
        _source = new HwndSource(new HwndSourceParameters("WinDeployClipMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,   // message-only window: no UI, just receives WM_CLIPBOARDUPDATE
        });
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
        Running = true;
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { if (_source != null) RemoveClipboardFormatListener(_source.Handle); } catch { }
        try { _source?.RemoveHook(WndProc); } catch { }
        try { _source?.Dispose(); } catch { }
        _source = null;
    }

    /// <summary>Ignore the next clipboard change whose content matches <paramref name="hash"/> — used right
    /// before we write a remote entry onto the local clipboard so it isn't re-broadcast.</summary>
    public void Suppress(string hash) => _suppressHash = hash;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnClipboardUpdate();
        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        var entry = Capture();
        if (entry == null) return;

        var hash = entry.ContentHash();
        if (_suppressHash != null && hash == _suppressHash) { _suppressHash = null; _lastHash = hash; return; }
        if (hash == _lastHash) return;   // same content copied again — don't duplicate
        _lastHash = hash;
        Captured?.Invoke(entry);
    }

    /// <summary>Read the current clipboard as a text or image entry; null if empty / unreadable / too large.
    /// Retries briefly because another app may momentarily hold the clipboard open.</summary>
    private ClipEntry? Capture()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (string.IsNullOrEmpty(text)) return null;
                    return new ClipEntry { Kind = ClipKind.Text, Text = text, CreatedAtUnix = ClipEntry.NowUnix() };
                }
                if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img == null) return null;
                    var png = EncodePng(img);
                    if (png.Length > MaxImageBytes)
                    {
                        Log?.Invoke($"剪贴板图片过大（{png.Length / 1024} KB），已跳过同步");
                        return null;
                    }
                    return new ClipEntry
                    {
                        Kind = ClipKind.Image, Image = png,
                        ImageW = img.PixelWidth, ImageH = img.PixelHeight, CreatedAtUnix = ClipEntry.NowUnix(),
                    };
                }
                return null;   // not text/image (files, etc.) — out of scope for v1
            }
            catch (COMException) { System.Threading.Thread.Sleep(30); }   // clipboard busy — retry
            catch { return null; }
        }
        return null;
    }

    private static byte[] EncodePng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}
