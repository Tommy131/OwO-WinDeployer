using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OwOWinDeployer.App.Services.Audio;
using OwOWinDeployer.App.Services.Infra;

namespace OwOWinDeployer.App.Views.Audio;

/// <summary>The desktop "Audio Waveform" widget — a borderless, frosted-glass, rounded panel that floats on the
/// desktop and renders a live visualization of the system's audio output (WASAPI loopback). It reuses the exact
/// glass mechanics of the Quick Launch widget (grab the screen behind → BlurEffect → one rounded clip over blur +
/// tint + content), so the two gadgets look like siblings. Capture runs only while the widget is visible.
///
/// Draggable by its header; remembers its position; header buttons cycle the style/colour on the fly; the ✕ asks
/// the owner to turn the widget off.</summary>
public partial class AudioWaveWidgetWindow : Window
{
    /// <summary>Raised when the user clicks the ✕. The owner (MainWindow) flips the setting off and hides us.</summary>
    public event Action? CloseRequested;

    // Overscan (DIP) so the BlurEffect has real neighbouring pixels beyond the visible edge. Matches the blur radius.
    private const double BlurMargin = 34;

    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly AudioCaptureService _capture;
    private readonly WaveformRenderer _renderer;
    private readonly MediaSessionService _media;     // "now playing" from Windows SMTC (+ album-art colour)
    // The shared Settings view-model (same instance the Settings page binds to). The header's style/colour
    // buttons drive it — not SettingsStore directly — so the page's ComboBoxes stay in sync with the widget.
    private readonly SettingsViewModel _settings;

    private bool _placed;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _readoutTimer;   // updates the note · BPM · level readout ~4×/s
    private readonly DispatcherTimer _progressTimer;  // advances the now-playing progress bar
    private readonly bool _native;

    public AudioWaveWidgetWindow(SettingsViewModel settings)
    {
        InitializeComponent();
        _settings = settings;
        _capture = new AudioCaptureService(_analyzer);
        _renderer = new WaveformRenderer(_analyzer);
        VizHost.Child = _renderer;
        _media = new MediaSessionService(Dispatcher);
        _media.Changed += OnNowPlaying;
        _native = SettingsStore.Load().AudioWidgetNativeGlass;

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); CaptureBackground(); };
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _refresh.Tick += (_, _) => CaptureBackground();
        _readoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _readoutTimer.Tick += (_, _) => UpdateReadout();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) => UpdateProgress();

        Header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } } };
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        PinButton.Click += (_, _) => SetPinned(!Topmost);
        StyleButton.Click += (_, _) => CycleStyle();
        ColorButton.Click += (_, _) => CycleColor();

        SetPinned(SettingsStore.Load().AudioWidgetPinned, save: false);
        ApplyTint();
        ApplyVisualSettings();

        SourceInitialized += (_, _) =>
        {
            HideFromAltTab();
            ApplyClickThrough(SettingsStore.Load().AudioWidgetClickThrough);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(HitTestHook);   // edge-drag resize
            if (_native) ScreenBlur.EnableBlurBehind(this);
            else ScreenBlur.ExcludeFromCapture(this);
        };
        Loaded += (_, _) =>
        {
            PlaceFromSettingsOrDefault();
            _placed = true;
            if (_native) RootGrid.Clip = null;
            else { UpdateClip(); CaptureBackground(); }
        };
        SizeChanged += (_, _) => { SaveSize(); if (_native) return; UpdateClip(); ScheduleCapture(); };
        LocationChanged += (_, _) => { SavePosition(); if (!_native) ScheduleCapture(); };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ApplyTint();
                StartAudio();
                if (_native) ScreenBlur.EnableBlurBehind(this);
                else { CaptureBackground(); _refresh.Start(); }
            }
            else
            {
                StopAudio();
                if (!_native) _refresh.Stop();
            }
        };

        Loaded += (_, _) => _media.Start();     // begin tracking the current media session

        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _media.Changed -= OnNowPlaying;
            _media.Dispose();
            _renderer.Stop();
            _capture.Dispose();
        };
    }

    /// <summary>Update the "now playing" strip and feed the album-art colour to the renderer (recolouring live when
    /// the "album" scheme is active). Runs on the UI thread (the service marshals its events here).</summary>
    private void OnNowPlaying(NowPlaying np)
    {
        NowPlayingBar.Visibility = np.HasTrack ? Visibility.Visible : Visibility.Collapsed;
        TrackTitle.Text = np.Title;
        TrackArtist.Text = np.Artist;
        ArtBrush.ImageSource = np.Art;
        if (np.Accent is { } ac) ProgFill.Background = new SolidColorBrush(ac);   // tint progress to the album colour
        _renderer.AlbumColor = np.Accent;
        if (_renderer.Color == WaveColor.Album)
            _renderer.Apply(_renderer.Kind, WaveColor.Album, _renderer.Sensitivity);
        if (np.HasTrack && IsVisible) { _progressTimer.Start(); UpdateProgress(); }
        else _progressTimer.Stop();
    }

    /// <summary>Advance the now-playing progress bar + time label from the media session's timeline.</summary>
    private void UpdateProgress()
    {
        var (pos, dur) = _media.GetProgress();
        if (dur <= 0) { ProgFill.Width = 0; ProgTime.Text = ""; return; }
        ProgFill.Width = Math.Max(0, ProgTrack.ActualWidth * Math.Clamp(pos / dur, 0, 1));
        ProgTime.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
    }

    private static string FormatTime(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        int t = (int)sec;
        return $"{t / 60}:{t % 60:00}";
    }

    // ── audio lifecycle (capture only runs while visible) ──────────────────────
    private void StartAudio()
    {
        _capture.Start();
        _renderer.Start();
        NoDeviceHint.Visibility = _capture.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsStore.Load().AudioWidgetReadout && _capture.IsRunning) _readoutTimer.Start();
        if (_media.Current.HasTrack) { _progressTimer.Start(); UpdateProgress(); }
    }

    private void StopAudio()
    {
        _renderer.Stop();
        _capture.Stop();
        _readoutTimer.Stop();
        _progressTimer.Stop();
        HideReadout();
    }

    /// <summary>Refresh the corner readout: dominant musical note · estimated BPM · level meter.</summary>
    private void UpdateReadout()
    {
        if (!SettingsStore.Load().AudioWidgetReadout || !_analyzer.HasSignal) { HideReadout(); return; }
        var parts = new System.Collections.Generic.List<string>(3);
        var note = NoteName(_analyzer.DominantHz);
        if (note != null) parts.Add(note);
        if (_analyzer.Bpm > 0) parts.Add($"{_analyzer.Bpm} BPM");
        int bars = (int)Math.Round(_analyzer.Level * 5);
        parts.Add(new string('▮', bars) + new string('▯', 5 - bars));
        Readout.Text = string.Join("  ·  ", parts);
        ReadoutChip.Visibility = Readout.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideReadout() { Readout.Text = ""; ReadoutChip.Visibility = Visibility.Collapsed; }

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Nearest musical note name (e.g. "A4") for a frequency, or null when there's no clear tone.</summary>
    private static string? NoteName(float hz)
    {
        if (hz < 20f || hz > 8000f) return null;
        int midi = (int)Math.Round(69 + 12 * Math.Log2(hz / 440.0));
        if (midi < 12 || midi > 120) return null;
        return NoteNames[midi % 12] + (midi / 12 - 1);
    }

    /// <summary>Re-read the visual settings (style / colour / sensitivity) and push them to the renderer. Called on
    /// construction and by the owner when the user changes them on the Settings page.</summary>
    public void ApplyVisualSettings()
    {
        var s = SettingsStore.Load();
        _renderer.BeatReactive = s.AudioWidgetBeatReactive;
        _renderer.SilenceFade = s.AudioWidgetSilenceFade;
        _renderer.GlowScale = (float)Math.Clamp(s.AudioWidgetGlow, 0.2, 2.5);
        _renderer.HueDrift = s.AudioWidgetHueDrift;
        _renderer.Apply(
            AudioWaveOptions.ParseStyle(s.AudioWidgetStyle),
            AudioWaveOptions.ParseColor(s.AudioWidgetColor),
            (float)s.AudioWidgetSensitivity);
        if (s.AudioWidgetReadout && _capture.IsRunning) _readoutTimer.Start();
        else { _readoutTimer.Stop(); HideReadout(); }
        ApplyClickThrough(s.AudioWidgetClickThrough);
    }

    private const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Toggle mouse click-through (WS_EX_TRANSPARENT): when on, clicks pass through to the desktop and the
    /// widget becomes purely decorative (turn it back off from the Settings page).</summary>
    private void ApplyClickThrough(bool on)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;   // applied again at SourceInitialized once the handle exists
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }
        catch { /* non-critical */ }
    }

    // Advance style/colour via the shared view-model. Its setter persists the choice, notifies the Settings
    // page's bound ComboBox, and raises AudioWidgetVisualChanged — which the owner routes back to
    // ApplyVisualSettings() to update the renderer. One source of truth, both directions in sync.
    private void CycleStyle() => _settings.AudioStyleIndex = (_settings.AudioStyleIndex + 1) % AudioWaveOptions.StyleCount;

    private void CycleColor() => _settings.AudioColorIndex = (_settings.AudioColorIndex + 1) % AudioWaveOptions.ColorCount;

    private void OnThemeChanged()
    {
        ApplyTint();
        ApplyVisualSettings();               // rebuilds the accent-derived palette + glow
        if (_native) ScreenBlur.EnableBlurBehind(this);
        else CaptureBackground();
    }

    private void UpdateClip() => RoundClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
    private void ScheduleCapture() { _settle.Stop(); _settle.Start(); }

    private void CaptureBackground()
    {
        if (_native || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
        var ps = PresentationSource.FromVisual(this);
        if (ps?.CompositionTarget == null) return;

        var m = ps.CompositionTarget.TransformToDevice;
        double sx = m.M11, sy = m.M22;
        int xPx = (int)Math.Round((Left - BlurMargin) * sx);
        int yPx = (int)Math.Round((Top - BlurMargin) * sy);
        int wPx = (int)Math.Round((ActualWidth + 2 * BlurMargin) * sx);
        int hPx = (int)Math.Round((ActualHeight + 2 * BlurMargin) * sy);

        var bmp = ScreenBlur.Capture(xPx, yPx, wPx, hPx);
        if (bmp == null) return;
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        brush.Freeze();
        BgLayer.Background = brush;
        BgLayer.Margin = new Thickness(-BlurMargin);
    }

    /// <summary>Recompute the material tint from the current theme + the user's see-through setting.</summary>
    public void ApplyTint()
    {
        var op = SettingsStore.Load().AudioWidgetOpacity;
        var a = (byte)Math.Clamp((int)Math.Round(op * 255), 0, 255);
        var c = ThemeManager.IsDark ? Color.FromArgb(a, 0x18, 0x1A, 0x1F) : Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
        TintLayer.Background = new SolidColorBrush(c);
    }

    private void SetPinned(bool pinned, bool save = true)
    {
        Topmost = pinned;
        PinButton.Foreground = pinned
            ? (Application.Current.TryFindResource("Accent") as Brush ?? Brushes.SteelBlue)
            : (Application.Current.TryFindResource("TextSecondary") as Brush ?? Brushes.Gray);
        PinButton.Opacity = pinned ? 1.0 : 0.7;
        if (!save) return;
        var s = SettingsStore.Load();
        s.AudioWidgetPinned = pinned;
        SettingsStore.Save(s);
    }

    private void PlaceFromSettingsOrDefault()
    {
        var s = SettingsStore.Load();
        var wa = SystemParameters.WorkArea;
        if (s.AudioWidgetWidth is { } sw && sw >= MinWidth) Width = Math.Min(sw, MaxWidth);
        if (s.AudioWidgetHeight is { } sh && sh >= MinHeight) Height = Math.Min(sh, MaxHeight);
        if (s.AudioWidgetLeft is { } l && s.AudioWidgetTop is { } t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
        }
        else
        {
            Left = wa.Right - Width - 24;
            Top = wa.Top + 24 + 200;      // below the Quick Launch widget's default slot
        }
    }

    private static bool IsOnScreen(double left, double top)
        => left > SystemParameters.VirtualScreenLeft - 200
        && top > SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void SavePosition()
    {
        if (!_placed) return;
        var s = SettingsStore.Load();
        s.AudioWidgetLeft = Left;
        s.AudioWidgetTop = Top;
        SettingsStore.Save(s);
    }

    private void SaveSize()
    {
        if (!_placed) return;
        var s = SettingsStore.Load();
        s.AudioWidgetWidth = ActualWidth;
        s.AudioWidgetHeight = ActualHeight;
        SettingsStore.Save(s);
    }

    // ── keep the gadget out of Alt+Tab (WS_EX_TOOLWINDOW) ──
    private void HideFromAltTab()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }
        catch { /* non-critical */ }
    }

    // ── edge-drag resize on the borderless window (WM_NCHITTEST) ──
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>Report the window edges/corners as resize zones so the borderless glass widget can be dragged
    /// bigger/smaller; everything else stays client (so the header still drags the whole window).</summary>
    private IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;
        if (SettingsStore.Load().AudioWidgetClickThrough) return IntPtr.Zero;   // don't grab when click-through
        if (!GetWindowRect(hwnd, out var r)) return IntPtr.Zero;
        int x = unchecked((short)(long)lParam);
        int y = unchecked((short)((long)lParam >> 16));
        const int m = 8;
        bool left = x < r.L + m, right = x > r.R - m, top = y < r.T + m, bottom = y > r.B - m;
        int ht = (top, bottom, left, right) switch
        {
            (true, _, true, _) => HTTOPLEFT,
            (true, _, _, true) => HTTOPRIGHT,
            (_, true, true, _) => HTBOTTOMLEFT,
            (_, true, _, true) => HTBOTTOMRIGHT,
            (true, _, _, _) => HTTOP,
            (_, true, _, _) => HTBOTTOM,
            (_, _, true, _) => HTLEFT,
            (_, _, _, true) => HTRIGHT,
            _ => HTCLIENT,
        };
        if (ht == HTCLIENT) return IntPtr.Zero;   // let normal processing (header drag etc.) run
        handled = true;
        return new IntPtr(ht);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
