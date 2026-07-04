namespace OwOWinDeployer.App.Services.Audio;

/// <summary>How the desktop audio widget draws the sound. All styles read the same live capture; only the
/// visual mapping differs. Persisted as the lowercase token (see <see cref="AudioWaveOptions.ParseStyle"/>).</summary>
public enum WaveStyle
{
    /// <summary>Frequency spectrum mirrored above and below the centre line (the default — most "musical").</summary>
    Mirror,
    /// <summary>Frequency spectrum as bars rising from the bottom.</summary>
    Bars,
    /// <summary>The raw waveform drawn as a glowing oscilloscope trace.</summary>
    Oscilloscope,
    /// <summary>Frequency spectrum arranged radially around a circle.</summary>
    Radial,
    /// <summary>The waveform as a filled, glowing gradient ribbon (area under the trace).</summary>
    Ribbon,
    /// <summary>A scrolling spectrogram waterfall — frequency (x) over time (y) as a heatmap.</summary>
    Spectrogram,
    /// <summary>The oscilloscope waveform wrapped around a circle — a pulsing polar ring.</summary>
    Polar,
    /// <summary>A particle field pushed outward by the bass / beats.</summary>
    Particles,
    /// <summary>Retro segmented VU meters for the bass / mid / treble bands, with falling peak holds.</summary>
    Vu,
    /// <summary>A smooth, glowing blob that bulges with the low frequencies.</summary>
    Blob,
    /// <summary>Two oscilloscope traces — left channel on top, right channel on the bottom (stereo).</summary>
    DualChannel,
}

/// <summary>Colour scheme for the bars/trace. Accent follows the app theme; the rest are fixed gradients.</summary>
public enum WaveColor
{
    /// <summary>The app's accent colour, brighter toward the peaks.</summary>
    Accent,
    /// <summary>Hue swept across the spectrum (low = red, high = violet).</summary>
    Rainbow,
    /// <summary>Deep red → orange → yellow, mapped by bar height.</summary>
    Fire,
    /// <summary>Navy → cyan → aqua, mapped by bar height.</summary>
    Ocean,
    /// <summary>The dominant colour of the currently-playing track's album art (falls back to the theme accent).</summary>
    Album,
}

/// <summary>Parsing helpers between the persisted lowercase tokens (in settings.json) and the enums, with a safe
/// fallback so a hand-edited or future-version token never throws.</summary>
public static class AudioWaveOptions
{
    public static WaveStyle ParseStyle(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "bars" => WaveStyle.Bars,
        "scope" or "oscilloscope" => WaveStyle.Oscilloscope,
        "radial" => WaveStyle.Radial,
        "ribbon" => WaveStyle.Ribbon,
        "spectrogram" or "spectro" => WaveStyle.Spectrogram,
        "polar" => WaveStyle.Polar,
        "particles" => WaveStyle.Particles,
        "vu" => WaveStyle.Vu,
        "blob" => WaveStyle.Blob,
        "dual" or "stereo" => WaveStyle.DualChannel,
        _ => WaveStyle.Mirror,
    };

    public static string ToToken(WaveStyle s) => s switch
    {
        WaveStyle.Bars => "bars",
        WaveStyle.Oscilloscope => "scope",
        WaveStyle.Radial => "radial",
        WaveStyle.Ribbon => "ribbon",
        WaveStyle.Spectrogram => "spectrogram",
        WaveStyle.Polar => "polar",
        WaveStyle.Particles => "particles",
        WaveStyle.Vu => "vu",
        WaveStyle.Blob => "blob",
        WaveStyle.DualChannel => "dual",
        _ => "mirror",
    };

    /// <summary>Number of wave styles / colour schemes — for cycling and combo-box bounds.</summary>
    public static int StyleCount => Enum.GetValues<WaveStyle>().Length;
    public static int ColorCount => Enum.GetValues<WaveColor>().Length;

    public static WaveColor ParseColor(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "rainbow" => WaveColor.Rainbow,
        "fire" => WaveColor.Fire,
        "ocean" => WaveColor.Ocean,
        "album" => WaveColor.Album,
        _ => WaveColor.Accent,
    };

    public static string ToToken(WaveColor c) => c switch
    {
        WaveColor.Rainbow => "rainbow",
        WaveColor.Fire => "fire",
        WaveColor.Ocean => "ocean",
        WaveColor.Album => "album",
        _ => "accent",
    };
}
