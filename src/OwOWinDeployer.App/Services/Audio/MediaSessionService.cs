using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OwOWinDeployer.App.Services.Audio;

/// <summary>What's playing right now, per the Windows System Media Transport Controls (the same source that powers
/// the media flyout). <see cref="Accent"/> is the dominant vibrant colour extracted from the album art (for the
/// "album" colour scheme).</summary>
public sealed record NowPlaying(string Title, string Artist, ImageSource? Art, Color? Accent, bool IsPlaying)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
    public static readonly NowPlaying None = new("", "", null, null, false);
}

/// <summary>Reads the current media session (title / artist / album art / play state) from Windows' SMTC and raises
/// <see cref="Changed"/> on the UI thread whenever it changes. All WinRT calls are best-effort and wrapped — a
/// machine with no active media session simply yields <see cref="NowPlaying.None"/>.</summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public event Action<NowPlaying>? Changed;
    public NowPlaying Current { get; private set; } = NowPlaying.None;

    public MediaSessionService(Dispatcher ui) => _ui = ui;

    public async void Start()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _mgr.CurrentSessionChanged += (_, _) => HookSession();
            HookSession();
        }
        catch { /* SMTC unavailable (older Windows / no media) — stay at None */ }
    }

    private void HookSession()
    {
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnChanged;
                _session.PlaybackInfoChanged -= OnChanged;
            }
            _session = _mgr?.GetCurrentSession();
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnChanged;
                _session.PlaybackInfoChanged += OnChanged;
            }
            _ = RefreshAsync();
        }
        catch { /* transient */ }
    }

    private void OnChanged(object? s, object? e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var s = _session;
            if (s == null) { Publish(NowPlaying.None); return; }

            var props = await s.TryGetMediaPropertiesAsync();
            bool playing = false;
            try { playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { }

            (ImageSource? art, Color? accent) = props?.Thumbnail != null ? await LoadArtAsync(props.Thumbnail) : (null, null);
            Publish(new NowPlaying(props?.Title ?? "", props?.Artist ?? "", art, accent, playing));
        }
        catch { /* keep last known */ }
    }

    /// <summary>Current playback position and total duration in seconds (0,0 when unknown). While playing, the
    /// position is extrapolated from the last SMTC update so a progress bar advances smoothly between updates.</summary>
    public (double pos, double dur) GetProgress()
    {
        try
        {
            var s = _session;
            if (s == null) return (0, 0);
            var tl = s.GetTimelineProperties();
            double dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            double pos = (tl.Position - tl.StartTime).TotalSeconds;
            if (Current.IsPlaying && tl.LastUpdatedTime != default)
                pos += Math.Max(0, (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds);
            if (dur > 0) pos = Math.Clamp(pos, 0, dur);
            return (pos < 0 ? 0 : pos, dur < 0 ? 0 : dur);
        }
        catch { return (0, 0); }
    }

    private async Task<(ImageSource?, Color?)> LoadArtAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream == null || stream.Size == 0) return (null, null);
            var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            return await _ui.InvokeAsync<(ImageSource?, Color?)>(() =>
            {
                var bmp = new BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return (bmp, DominantColor(bmp));
            });
        }
        catch { return (null, null); }
    }

    /// <summary>Pick the most vibrant colour of a bitmap: downscale to a tiny grid and choose the pixel with the
    /// highest saturation×value (weighted), so the album's signature colour drives the visualizer.</summary>
    private static Color DominantColor(BitmapSource src)
    {
        try
        {
            double scale = 24.0 / Math.Max(1, Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource small = scale < 1 ? new TransformedBitmap(src, new ScaleTransform(scale, scale)) : src;
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double bestScore = -1; byte br = 0x2E, bg = 0x9B, bb = 0xF0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double v = max / 255.0, sat = max == 0 ? 0 : (max - min) / (double)max;
                double score = sat * 1.4 + v * 0.6;                   // favour saturated, reasonably bright
                if (v < 0.15) continue;                                // skip near-black
                if (score > bestScore) { bestScore = score; br = r; bg = g; bb = b; }
            }
            return Color.FromRgb(br, bg, bb);
        }
        catch { return Color.FromRgb(0x2E, 0x9B, 0xF0); }
    }

    private void Publish(NowPlaying np)
    {
        Current = np;
        _ui.InvokeAsync(() => { if (!_disposed) Changed?.Invoke(np); });
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnChanged;
                _session.PlaybackInfoChanged -= OnChanged;
            }
        }
        catch { }
    }
}
