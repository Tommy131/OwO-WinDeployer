using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OwOWinDeployer.App.Services.Infra;
using OwOWinDeployer.App.ViewModels.Launch;

namespace OwOWinDeployer.App.Views.Launch;

/// <summary>The desktop "Quick Launch" widget — a borderless, frosted-glass, rounded panel that floats on the
/// desktop and shares the page's <see cref="LaunchCenterViewModel"/>, so its list stays in sync with edits made
/// on the page. The frosted glass is done in pure WPF (grab the screen behind → BlurEffect → one smooth rounded
/// clip over blur + tint + content), which gives real see-through blur AND anti-aliased corners together.
/// Draggable by its header; remembers its position; the ✕ asks the owner to turn the widget off.</summary>
public partial class LauncherWidgetWindow : Window
{
    /// <summary>Raised when the user clicks the ✕. The owner (MainWindow) flips the setting off and hides us.</summary>
    public event Action? CloseRequested;

    // Overscan (DIP) so the BlurEffect has real neighbouring pixels beyond the visible edge (no dark fade). Matches
    // the blur radius; the rounded clip hides the overscan ring.
    private const double BlurMargin = 34;

    private bool _placed;                 // suppress position saves until initial placement is done
    private readonly DispatcherTimer _settle;   // coalesce rapid moves/resizes into one capture
    private readonly DispatcherTimer _refresh;  // periodic re-capture so the glass stays fresh while shown
    // Glass mode chosen at build time: true = native DWM blur-behind (instant, square); false = WPF capture blur
    // (smooth rounded, slight drag lag). Switching the setting rebuilds the window (MainWindow.RecreateDesktopWidget).
    private readonly bool _native;

    public LauncherWidgetWindow(LaunchCenterViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _native = SettingsStore.Load().WidgetNativeGlass;

        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _settle.Tick += (_, _) => { _settle.Stop(); CaptureBackground(); };
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _refresh.Tick += (_, _) => CaptureBackground();

        // Drag the whole thing by its header.
        Header.MouseLeftButtonDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } } };
        CloseButton.Click += (_, _) => CloseRequested?.Invoke();
        PinButton.Click += (_, _) => SetPinned(!Topmost);

        SetPinned(SettingsStore.Load().WidgetPinned, save: false);
        ApplyTint();

        SourceInitialized += (_, _) =>
        {
            HideFromAltTab();
            if (_native) ScreenBlur.EnableBlurBehind(this);           // native: instant DWM blur, square corners
            else ScreenBlur.ExcludeFromCapture(this);                 // wpf: grab what's BEHIND us, not ourselves
        };
        Loaded += (_, _) =>
        {
            PlaceFromSettingsOrDefault();
            _placed = true;
            if (_native) RootGrid.Clip = null;                        // native mode is square (no rounded clip)
            else { UpdateClip(); CaptureBackground(); }
        };
        SizeChanged += (_, _) => { if (_native) return; UpdateClip(); ScheduleCapture(); };
        LocationChanged += (_, _) => { SavePosition(); if (!_native) ScheduleCapture(); };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ApplyTint();
                if (_native) ScreenBlur.EnableBlurBehind(this);
                else { CaptureBackground(); _refresh.Start(); }
            }
            else if (!_native) _refresh.Stop();
        };

        // Live-refresh the code-set tint (and the WPF blur capture) when the app theme changes — DynamicResource
        // brushes update themselves, but the tint fill and captured background are set in code, so re-apply here.
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        ApplyTint();
        if (_native) ScreenBlur.EnableBlurBehind(this);
        else CaptureBackground();
    }

    /// <summary>Update the rounded clip that shapes every layer to the current window size.</summary>
    private void UpdateClip() => RoundClip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

    /// <summary>Debounce a capture — used during drag/resize so we grab once the motion settles.</summary>
    private void ScheduleCapture() { _settle.Stop(); _settle.Start(); }

    /// <summary>Grab the screen region behind the widget (the widget is excluded from capture), and show it as the
    /// blurred glass background. The captured area is overscanned by <see cref="BlurMargin"/> so the blur is clean
    /// to the edges.</summary>
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
        // A background brush (not an Image element) so the bitmap size never contributes to SizeToContent.
        var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
        brush.Freeze();
        BgLayer.Background = brush;
        BgLayer.Margin = new Thickness(-BlurMargin);
    }

    /// <summary>Recompute the material tint from the current theme + the user's see-through setting
    /// (设置 → 背景透视程度). Lower opacity → more of the blurred background shows through. In light mode a light
    /// tint keeps dark text readable; in dark mode a dark tint keeps light text readable.</summary>
    public void ApplyTint()
    {
        var op = SettingsStore.Load().WidgetOpacity;
        var a = (byte)Math.Clamp((int)Math.Round(op * 255), 0, 255);
        var c = ThemeManager.IsDark ? Color.FromArgb(a, 0x23, 0x25, 0x2A) : Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
        TintLayer.Background = new SolidColorBrush(c);
    }

    /// <summary>Toggle "always on top" (the 图钉 pin). Highlights the pin when active and persists the choice.</summary>
    private void SetPinned(bool pinned, bool save = true)
    {
        Topmost = pinned;
        PinButton.Foreground = pinned
            ? (Application.Current.TryFindResource("Accent") as Brush ?? Brushes.SteelBlue)
            : (Application.Current.TryFindResource("TextSecondary") as Brush ?? Brushes.Gray);
        PinButton.Opacity = pinned ? 1.0 : 0.7;
        if (!save) return;
        var s = SettingsStore.Load();
        s.WidgetPinned = pinned;
        SettingsStore.Save(s);
    }

    private void PlaceFromSettingsOrDefault()
    {
        var s = SettingsStore.Load();
        var wa = SystemParameters.WorkArea;
        if (s.WidgetLeft is { } l && s.WidgetTop is { } t && IsOnScreen(l, t))
        {
            Left = l; Top = t;
        }
        else
        {
            Left = wa.Right - Width - 24;      // default: top-right of the work area
            Top = wa.Top + 24;
        }
    }

    /// <summary>Keep at least a sliver on a visible monitor, so a saved position from an unplugged second screen
    /// doesn't strand the widget off-screen.</summary>
    private static bool IsOnScreen(double left, double top)
        => left > SystemParameters.VirtualScreenLeft - 200
        && top > SystemParameters.VirtualScreenTop - 50
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

    private void SavePosition()
    {
        if (!_placed) return;
        var s = SettingsStore.Load();
        s.WidgetLeft = Left;
        s.WidgetTop = Top;
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
