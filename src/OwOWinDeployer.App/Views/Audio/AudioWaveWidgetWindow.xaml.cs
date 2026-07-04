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
    // The shared Settings view-model (same instance the Settings page binds to). The header's style/colour
    // buttons drive it — not SettingsStore directly — so the page's ComboBoxes stay in sync with the widget.
    private readonly SettingsViewModel _settings;

    private bool _placed;
    private readonly DispatcherTimer _settle;
    private readonly DispatcherTimer _refresh;
    private readonly bool _native;

    public AudioWaveWidgetWindow(SettingsViewModel settings)
    {
        InitializeComponent();
        _settings = settings;
        _capture = new AudioCaptureService(_analyzer);
        _renderer = new WaveformRenderer(_analyzer);
        VizHost.Child = _renderer;
        _native = SettingsStore.Load().AudioWidgetNativeGlass;

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); CaptureBackground(); };
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _refresh.Tick += (_, _) => CaptureBackground();

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
        SizeChanged += (_, _) => { if (_native) return; UpdateClip(); ScheduleCapture(); };
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

        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _renderer.Stop();
            _capture.Dispose();
        };
    }

    // ── audio lifecycle (capture only runs while visible) ──────────────────────
    private void StartAudio()
    {
        _capture.Start();
        _renderer.Start();
        NoDeviceHint.Visibility = _capture.IsRunning ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StopAudio()
    {
        _renderer.Stop();
        _capture.Stop();
    }

    /// <summary>Re-read the visual settings (style / colour / sensitivity) and push them to the renderer. Called on
    /// construction and by the owner when the user changes them on the Settings page.</summary>
    public void ApplyVisualSettings()
    {
        var s = SettingsStore.Load();
        _renderer.BeatReactive = s.AudioWidgetBeatReactive;
        _renderer.SilenceFade = s.AudioWidgetSilenceFade;
        _renderer.Apply(
            AudioWaveOptions.ParseStyle(s.AudioWidgetStyle),
            AudioWaveOptions.ParseColor(s.AudioWidgetColor),
            (float)s.AudioWidgetSensitivity);
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

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
