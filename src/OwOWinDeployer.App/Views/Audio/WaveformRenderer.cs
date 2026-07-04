using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using OwOWinDeployer.App.Services.Audio;

namespace OwOWinDeployer.App.Views.Audio;

/// <summary>The animated heart of the audio widget: a lightweight <see cref="FrameworkElement"/> that reads the live
/// spectrum from a <see cref="SpectrumAnalyzer"/> and paints it every frame. Driven by
/// <see cref="CompositionTarget.Rendering"/> (vsync-paced, ~60 fps) rather than a timer, so motion is buttery.
///
/// Visual smoothing lives here, decoupled from capture: bars snap up fast and fall slowly (attack/decay), with a
/// separate slower-falling peak marker — that's what makes a spectrum feel "alive" instead of jittery. Colours and
/// glow are precomputed on change (not per frame). Silence eases everything to zero, so no audio → a calm flat line.</summary>
public sealed class WaveformRenderer : FrameworkElement
{
    private const int BarCount = 56;
    private const int WavePoints = 320;

    private readonly SpectrumAnalyzer _analyzer;
    private readonly float[] _bars = new float[BarCount];      // instantaneous magnitudes
    private readonly float[] _display = new float[BarCount];   // eased for display
    private readonly float[] _peak = new float[BarCount];      // slowly-falling peak caps
    private readonly float[] _wave = new float[WavePoints];

    private const int SpecCols = 96;                           // spectrogram frequency columns

    private LinearGradientBrush[] _barBrushes = Array.Empty<LinearGradientBrush>();
    private Color[] _barColors = Array.Empty<Color>();
    private Pen[] _barPens = Array.Empty<Pen>();
    private readonly Brush _capBrush;
    private bool _running;

    private readonly float[] _spec = new float[SpecCols];      // current spectrum column (spectrogram)
    private WriteableBitmap? _specBmp;                          // scrolling waterfall bitmap
    private int[]? _specPixels;
    private int _specW, _specH;

    private Color _accent = System.Windows.Media.Color.FromRgb(0x2E, 0x9B, 0xF0);
    private Brush? _ribbonFill;
    private DropShadowEffect? _glow;
    private readonly ScaleTransform _scale = new(1, 1);
    private float _beat;                                        // smoothed beat pulse
    private float _fadeOpacity = 1f;                            // smoothed silence-fade opacity

    /// <summary>Which visualization to draw. Named <c>Kind</c> (not <c>Style</c>) to avoid shadowing
    /// <see cref="FrameworkElement.Style"/>.</summary>
    public WaveStyle Kind { get; set; } = WaveStyle.Mirror;
    public WaveColor Color { get; set; } = WaveColor.Accent;
    public float Sensitivity { get; set; } = 1f;

    /// <summary>Dominant album-art colour for the "Album" colour scheme (null → theme accent). Set by the widget
    /// from the media session; call <see cref="Apply"/> (or re-Apply) to rebuild the palette after it changes.</summary>
    public Color? AlbumColor { get; set; }

    /// <summary>React to detected beats with a glow/scale pulse.</summary>
    public bool BeatReactive { get; set; } = true;
    /// <summary>Dim the waveform when the audio goes quiet (a calm "breathing" idle).</summary>
    public bool SilenceFade { get; set; } = true;

    public WaveformRenderer(SpectrumAnalyzer analyzer)
    {
        _analyzer = analyzer;
        IsHitTestVisible = false;
        _capBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        _capBrush.Freeze();
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _scale;
        BuildPalette();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        Array.Clear(_display); Array.Clear(_peak); Array.Clear(_bars);
        _specBmp = null; _beat = 0; _fadeOpacity = 1f; Opacity = 1;
        _scale.ScaleX = _scale.ScaleY = 1;
        InvalidateVisual();
    }

    /// <summary>Re-read style/colour/sensitivity (call after the user changes them or the theme flips) and rebuild
    /// the cached brushes/pens so the next frame reflects it.</summary>
    public void Apply(WaveStyle style, WaveColor color, float sensitivity)
    {
        Kind = style; Color = color; Sensitivity = Math.Clamp(sensitivity, 0.3f, 3f);
        _specBmp = null;                 // fresh spectrogram on any style/colour change
        BuildPalette();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        _analyzer.Update(Environment.TickCount64);

        switch (Kind)
        {
            case WaveStyle.Oscilloscope:
            case WaveStyle.Ribbon:
            case WaveStyle.Polar:
                _analyzer.FillWave(_wave);
                break;
            case WaveStyle.Spectrogram:
                _analyzer.FillSpectrum(_spec, Sensitivity);
                break;
            default:
                _analyzer.FillBars(_bars, Sensitivity);
                for (int i = 0; i < BarCount; i++)
                {
                    float target = _bars[i], cur = _display[i];
                    // Fast attack, slow release — the classic responsive-but-smooth spectrum feel.
                    _display[i] = cur + (target - cur) * (target > cur ? 0.45f : 0.16f);
                    if (_display[i] >= _peak[i]) _peak[i] = _display[i];
                    else _peak[i] = Math.Max(_display[i], _peak[i] - 0.011f);
                }
                break;
        }

        // Beat → glow + subtle scale pulse.
        float beatTarget = BeatReactive ? _analyzer.BeatPulse : 0f;
        _beat += (beatTarget - _beat) * 0.5f;
        if (_glow != null) { _glow.BlurRadius = 16 + _beat * 22; _glow.Opacity = 0.8 + _beat * 0.2; }
        _scale.ScaleX = _scale.ScaleY = 1f + _beat * 0.035f;

        // Silence → breathe: dim the waveform when quiet, wake up when sound returns.
        float targetOp = SilenceFade ? Math.Clamp(0.32f + 1.5f * _analyzer.Level, 0.32f, 1f) : 1f;
        _fadeOpacity += (targetOp - _fadeOpacity) * 0.08f;
        Opacity = _fadeOpacity;

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        switch (Kind)
        {
            case WaveStyle.Bars: DrawBars(dc, w, h, mirror: false); break;
            case WaveStyle.Mirror: DrawBars(dc, w, h, mirror: true); break;
            case WaveStyle.Radial: DrawRadial(dc, w, h); break;
            case WaveStyle.Oscilloscope: DrawScope(dc, w, h); break;
            case WaveStyle.Ribbon: DrawRibbon(dc, w, h); break;
            case WaveStyle.Spectrogram: DrawSpectrogram(dc, w, h); break;
            case WaveStyle.Polar: DrawPolar(dc, w, h); break;
        }
    }

    // ── styles ────────────────────────────────────────────────────────────────
    private void DrawBars(DrawingContext dc, double w, double h, bool mirror)
    {
        double slot = w / BarCount;
        double bw = Math.Max(2, slot * 0.62);
        double pad = (slot - bw) / 2;
        double maxH = mirror ? (h / 2 - 3) : (h - 4);
        double baseY = mirror ? h / 2 : h - 2;
        double radius = Math.Min(bw / 2, 4);

        for (int i = 0; i < BarCount; i++)
        {
            double x = i * slot + pad;
            double bh = Math.Max(1.0, _display[i] * maxH);
            var brush = _barBrushes[i];
            if (mirror)
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(x, baseY - bh, bw, bh), radius, radius);
                dc.DrawRoundedRectangle(brush, null, new Rect(x, baseY, bw, bh), radius, radius);
                double py = baseY - _peak[i] * maxH;
                dc.DrawRoundedRectangle(_capBrush, null, new Rect(x, py - 1.5, bw, 2.2), 1, 1);
                dc.DrawRoundedRectangle(_capBrush, null, new Rect(x, baseY + _peak[i] * maxH - 0.7, bw, 2.2), 1, 1);
            }
            else
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(x, baseY - bh, bw, bh), radius, radius);
                double py = baseY - _peak[i] * maxH;
                dc.DrawRoundedRectangle(_capBrush, null, new Rect(x, py - 1.5, bw, 2.2), 1, 1);
            }
        }
    }

    private void DrawRadial(DrawingContext dc, double w, double h)
    {
        var c = new Point(w / 2, h / 2);
        double inner = Math.Min(w, h) * 0.17;
        double maxLen = Math.Min(w, h) * 0.30;
        for (int i = 0; i < BarCount; i++)
        {
            double ang = -Math.PI / 2 + i / (double)BarCount * Math.PI * 2;
            double len = inner + Math.Max(1.5, _display[i] * maxLen);
            double ca = Math.Cos(ang), sa = Math.Sin(ang);
            var p0 = new Point(c.X + ca * inner, c.Y + sa * inner);
            var p1 = new Point(c.X + ca * len, c.Y + sa * len);
            dc.DrawLine(_barPens[i], p0, p1);
            // Peak dot on the ring.
            double pl = inner + _peak[i] * maxLen;
            dc.DrawEllipse(_capBrush, null, new Point(c.X + ca * pl, c.Y + sa * pl), 1.6, 1.6);
        }
    }

    private void DrawScope(DrawingContext dc, double w, double h)
    {
        double cy = h / 2, amp = h / 2 - 4;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, cy - _wave[0] * amp), false, false);
            for (int i = 1; i < WavePoints; i++)
            {
                double x = i / (double)(WavePoints - 1) * w;
                ctx.LineTo(new Point(x, cy - _wave[i] * amp), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, _scopePen ?? _barPens[0], geo);
    }

    private void DrawRibbon(DrawingContext dc, double w, double h)
    {
        double cy = h / 2, amp = h / 2 - 4;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, cy), isFilled: true, isClosed: true);
            for (int i = 0; i < WavePoints; i++)
            {
                double x = i / (double)(WavePoints - 1) * w;
                ctx.LineTo(new Point(x, cy - _wave[i] * amp), true, false);
            }
            ctx.LineTo(new Point(w, cy), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(_ribbonFill, _scopePen, geo);   // translucent fill + glowing top stroke
    }

    private void DrawPolar(DrawingContext dc, double w, double h)
    {
        var c = new Point(w / 2, h / 2);
        double baseR = Math.Min(w, h) * 0.24;
        double amp = Math.Min(w, h) * 0.20;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i <= WavePoints; i++)
            {
                int j = i % WavePoints;
                double ang = -Math.PI / 2 + i / (double)WavePoints * Math.PI * 2;
                double r = baseR + _wave[j] * amp;
                var p = new Point(c.X + Math.Cos(ang) * r, c.Y + Math.Sin(ang) * r);
                if (i == 0) ctx.BeginFigure(p, false, true);
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, _scopePen ?? _barPens[0], geo);
    }

    private void DrawSpectrogram(DrawingContext dc, double w, double h)
    {
        int pw = Math.Max(8, (int)Math.Round(w)), ph = Math.Max(8, (int)Math.Round(h));
        if (_specBmp == null || _specW != pw || _specH != ph)
        {
            _specW = pw; _specH = ph;
            _specBmp = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
            _specPixels = new int[pw * ph];
        }
        var px = _specPixels!;
        // Scroll the whole image down one row (newest spectrum lands at the top), then paint the new top row.
        Array.Copy(px, 0, px, pw, pw * (ph - 1));
        for (int x = 0; x < pw; x++)
        {
            double t = x / (double)pw;
            int idx = Math.Clamp((int)(t * _spec.Length), 0, _spec.Length - 1);
            px[x] = SpecColor(Color, t, _spec[idx]);
        }
        _specBmp.WritePixels(new Int32Rect(0, 0, pw, ph), px, pw * 4, 0);
        dc.DrawImage(_specBmp, new Rect(0, 0, w, h));
    }

    /// <summary>Map (frequency-column, intensity) to a packed BGRA pixel using the current colour scheme.</summary>
    private int SpecColor(WaveColor c, double t, float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        Color col = c switch
        {
            WaveColor.Rainbow => Hsv(t * 285, 0.85, 0.12 + 0.88 * v),
            WaveColor.Fire => Ramp(v, Rgb(0, 0, 0), Rgb(0x7A, 0x12, 0x12), Rgb(0xFF, 0x6A, 0x10), Rgb(0xFF, 0xE8, 0x6A)),
            WaveColor.Ocean => Ramp(v, Rgb(0, 0, 0), Rgb(0x08, 0x1B, 0x40), Rgb(0x1E, 0x7F, 0xD0), Rgb(0x5A, 0xF0, 0xF0)),
            _ => Ramp(v, Rgb(0, 0, 0), Mul(_accent, 0.5), _accent, Lighten(_accent, 0.6)),
        };
        return (0xFF << 24) | (col.R << 16) | (col.G << 8) | col.B;
    }

    private static Color Ramp(float v, Color a, Color b, Color c, Color d)
    {
        if (v <= 0f) return a;
        if (v >= 1f) return d;
        float seg = v * 3f; int i = (int)seg; float f = seg - i;
        return i switch { 0 => Lerp(a, b, f), 1 => Lerp(b, c, f), _ => Lerp(c, d, f) };
    }

    private static Color Lerp(Color a, Color b, float t)
        => System.Windows.Media.Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color WithA(Color c, double a) => System.Windows.Media.Color.FromArgb((byte)(a * 255), c.R, c.G, c.B);

    // ── palette ─────────────────────────────────────────────────────────────
    private Pen? _scopePen;

    /// <summary>Rebuild the per-bar gradient brushes, solid colours and pens for the current colour scheme and the
    /// live theme accent. Called on construction and whenever style/colour/theme changes — never per frame.</summary>
    private void BuildPalette()
    {
        var accent = (Application.Current?.TryFindResource("Accent") as SolidColorBrush)?.Color
                     ?? System.Windows.Media.Color.FromRgb(0x2E, 0x9B, 0xF0);
        // "Album" scheme: recolour everything to the current track's dominant art colour (theme accent as fallback).
        if (Color == WaveColor.Album && AlbumColor is { } album) accent = album;

        _barBrushes = new LinearGradientBrush[BarCount];
        _barColors = new Color[BarCount];
        _barPens = new Pen[BarCount];

        for (int i = 0; i < BarCount; i++)
        {
            double t = i / (double)(BarCount - 1);
            (Color bottom, Color top, Color solid) = Color switch
            {
                WaveColor.Rainbow => RainbowStops(t),
                WaveColor.Fire => (Rgb(0xB7, 0x1C, 0x1C), Rgb(0xFF, 0xE0, 0x5A), Rgb(0xFF, 0x7A, 0x18)),
                WaveColor.Ocean => (Rgb(0x0A, 0x2A, 0x66), Rgb(0x46, 0xEC, 0xE8), Rgb(0x1E, 0x8F, 0xE0)),
                _ => (Mul(accent, 0.45), Lighten(accent, 0.55), accent),
            };
            var g = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1), EndPoint = new Point(0, 0),
            };
            g.GradientStops.Add(new GradientStop(bottom, 0));
            g.GradientStops.Add(new GradientStop(top, 1));
            g.Freeze();
            _barBrushes[i] = g;
            _barColors[i] = solid;
            var pen = new Pen(new SolidColorBrush(solid), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            _barPens[i] = pen;
        }

        // Oscilloscope: one horizontal gradient stroke across the width.
        var scope = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        if (Color == WaveColor.Rainbow)
        {
            scope.GradientStops.Add(new GradientStop(Rgb(0xFF, 0x45, 0x45), 0));
            scope.GradientStops.Add(new GradientStop(Rgb(0x45, 0xFF, 0x9E), 0.5));
            scope.GradientStops.Add(new GradientStop(Rgb(0x8A, 0x6C, 0xFF), 1));
        }
        else
        {
            var lo = _barColors[0]; var hi = _barColors[BarCount - 1];
            scope.GradientStops.Add(new GradientStop(lo, 0));
            scope.GradientStops.Add(new GradientStop(Lighten(hi, 0.4), 1));
        }
        scope.Freeze();
        var sp = new Pen(scope, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        sp.Freeze();
        _scopePen = sp;

        _accent = accent;

        // Filled-ribbon gradient (translucent, glowing) for the Ribbon style.
        var rlo = _barColors[0]; var rhi = _barColors[BarCount - 1];
        var ribbon = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        ribbon.GradientStops.Add(new GradientStop(WithA(Lighten(rhi, 0.3), 0.55), 0));
        ribbon.GradientStops.Add(new GradientStop(WithA(rlo, 0.28), 1));
        ribbon.Freeze();
        _ribbonFill = ribbon;

        // Neon glow over the whole drawing, tinted to the scheme; modulated per-frame by the beat.
        var glow = Color switch
        {
            WaveColor.Fire => Rgb(0xFF, 0x7A, 0x18),
            WaveColor.Ocean => Rgb(0x35, 0xE0, 0xE0),
            WaveColor.Rainbow => Rgb(0x9A, 0x6C, 0xFF),
            _ => accent,
        };
        _glow = new DropShadowEffect { Color = glow, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.85 };
        Effect = _glow;
    }

    private static (Color, Color, Color) RainbowStops(double t)
    {
        var solid = Hsv(t * 285, 0.85, 1.0);
        return (Hsv(t * 285, 0.9, 0.5), Hsv(t * 285, 0.7, 1.0), solid);
    }

    private static Color Rgb(byte r, byte g, byte b) => System.Windows.Media.Color.FromRgb(r, g, b);

    private static Color Mul(Color c, double f)
        => System.Windows.Media.Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color Lighten(Color c, double f)
        => System.Windows.Media.Color.FromRgb(
            (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    private static Color Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60.0;
        int hi = (int)h; double f = h - hi;
        double p = v * (1 - s), q = v * (1 - s * f), tt = v * (1 - s * (1 - f));
        double r, g, b;
        switch (hi)
        {
            case 0: r = v; g = tt; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = tt; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = tt; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return System.Windows.Media.Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
