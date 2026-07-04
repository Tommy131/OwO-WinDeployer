using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OwOWinDeployer.App.Services.Audio;

/// <summary>What's playing right now, per the Windows System Media Transport Controls (the same source that powers
/// the media flyout). <see cref="Accent"/> is the dominant vibrant colour extracted from the album art (for the
/// "album" colour scheme). <see cref="SourceCount"/> is how many apps currently expose media (YouTube in a browser,
/// a music player, …); <see cref="SourceIndex"/> is the 1-based position of the one shown — the widget uses these to
/// offer a source switcher when more than one app is playing.</summary>
public sealed record NowPlaying(string Title, string Artist, ImageSource? Art, Color? Accent, bool IsPlaying,
    bool CanPrev, bool CanNext, bool CanPlayPause, bool CanSeek, int SourceCount, int SourceIndex)
{
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
    public static readonly NowPlaying None = new("", "", null, null, false, false, false, false, false, 0, 0);
}

/// <summary>Reads the current media session (title / artist / album art / play state) from Windows' SMTC and raises
/// <see cref="Changed"/> on the UI thread whenever it changes. All WinRT calls are best-effort and wrapped — a
/// machine with no active media session simply yields <see cref="NowPlaying.None"/>.</summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;                                   // the one shown
    private readonly List<GlobalSystemMediaTransportControlsSession> _sessions = new();            // every source
    private string? _pinnedSourceId;   // source the user picked via SwitchSource; null = auto-follow the playing one
    private int _sourceCount, _sourceIndex;   // snapshot for NowPlaying (written on the UI thread in Hook)
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
            // A source starting/stopping fires SessionsChanged; the system's "current" pick changing fires
            // CurrentSessionChanged. Re-evaluate on both. Marshal to the UI thread so the session list is single-threaded.
            _mgr.CurrentSessionChanged += (_, _) => Post(Rebuild);
            _mgr.SessionsChanged += (_, _) => Post(Rebuild);
            Post(Rebuild);
        }
        catch { /* SMTC unavailable (older Windows / no media) — stay at None */ }
    }

    private void Post(Action a) => _ui.InvokeAsync(() => { if (!_disposed) a(); });

    /// <summary>Re-enumerate every media source (one per app) into a stable order, then (re)pick which to display.
    /// Runs on the UI thread so <see cref="_sessions"/> is only ever touched from one thread.</summary>
    private void Rebuild()
    {
        _sessions.Clear();
        try
        {
            var live = _mgr?.GetSessions();
            if (live != null) _sessions.AddRange(live);
        }
        catch { /* transient */ }
        // Stable order (by app id) so the switcher cycles predictably as tracks change.
        _sessions.Sort((a, b) => string.CompareOrdinal(a.SourceAppUserModelId, b.SourceAppUserModelId));
        Select();
    }

    /// <summary>Pick the session to show: the source the user pinned (if still present), else the first that is
    /// actually <i>playing</i> (so we never sit on a paused source while another plays), else Windows' current
    /// session, else the first available.</summary>
    private void Select()
    {
        GlobalSystemMediaTransportControlsSession? pick = null;
        if (_pinnedSourceId != null)
            pick = _sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pinnedSourceId);
        if (pick == null)
        {
            _pinnedSourceId = null;   // pinned source vanished → fall back to auto-follow
            pick = _sessions.FirstOrDefault(IsPlaying);
            if (pick == null) { try { pick = _mgr?.GetCurrentSession(); } catch { } }
            pick ??= _sessions.FirstOrDefault();
        }
        Hook(pick);
    }

    private void Hook(GlobalSystemMediaTransportControlsSession? pick)
    {
        _sourceCount = _sessions.Count;
        _sourceIndex = pick == null ? 0 : _sessions.FindIndex(s => ReferenceEquals(s, pick)) + 1;
        if (ReferenceEquals(_session, pick)) { _ = RefreshAsync(); return; }
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
        }
        _session = pick;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnChanged;
            _session.PlaybackInfoChanged += OnChanged;
        }
        _ = RefreshAsync();
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    /// <summary>Cycle the now-playing strip to the next media source and pin it (so it stays put until that source
    /// disappears, even if another app grabs the system's "current" slot). No-op with a single source.</summary>
    public void SwitchSource()
    {
        try
        {
            if (_sessions.Count <= 1) return;
            int cur = _session == null ? -1 : _sessions.FindIndex(s => ReferenceEquals(s, _session));
            var next = _sessions[(cur + 1) % _sessions.Count];
            _pinnedSourceId = next.SourceAppUserModelId;
            Hook(next);
        }
        catch { /* list shifted under us — next Rebuild will settle it */ }
    }

    private void OnChanged(object? s, object? e) => _ = RefreshAsync();

    // ── transport controls (best-effort; only work if the player supports them) ──
    public void SkipPrevious() => Fire(s => s.TrySkipPreviousAsync());
    public void SkipNext() => Fire(s => s.TrySkipNextAsync());
    public void TogglePlayPause() => Fire(s => s.TryTogglePlayPauseAsync());
    /// <summary>Seek to <paramref name="sec"/> seconds into the current track.</summary>
    public void Seek(double sec) => Fire(s => s.TryChangePlaybackPositionAsync((long)(Math.Max(0, sec) * TimeSpan.TicksPerSecond)));

    private void Fire(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> op)
    {
        try { var s = _session; if (s != null) _ = op(s); } catch { /* command not supported / failed */ }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var s = _session;
            if (s == null) { Publish(NowPlaying.None); return; }

            var props = await s.TryGetMediaPropertiesAsync();
            bool playing = false, cprev = false, cnext = false, cplay = false, cseek = false;
            try
            {
                var pb = s.GetPlaybackInfo();
                playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var ctl = pb?.Controls;
                cprev = ctl?.IsPreviousEnabled ?? false;
                cnext = ctl?.IsNextEnabled ?? false;
                cplay = (ctl?.IsPlayEnabled ?? false) || (ctl?.IsPauseEnabled ?? false) || (ctl?.IsPlayPauseToggleEnabled ?? false);
                cseek = ctl?.IsPlaybackPositionEnabled ?? false;
            }
            catch { }

            (ImageSource? art, Color? accent) = props?.Thumbnail != null ? await LoadArtAsync(props.Thumbnail) : (null, null);
            Publish(new NowPlaying(props?.Title ?? "", props?.Artist ?? "", art, accent, playing, cprev, cnext, cplay, cseek,
                _sourceCount, _sourceIndex));
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
