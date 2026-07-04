using NAudio.Dsp;

namespace OwOWinDeployer.App.Services.Audio;

/// <summary>Turns a rolling window of mono samples into a normalised frequency spectrum (for the bar/radial styles)
/// and exposes the raw waveform (for the oscilloscope style). The capture thread pushes samples via
/// <see cref="AddSamples"/>; the UI thread pulls a snapshot each frame via <see cref="FillBars"/> / <see cref="FillWave"/>.
///
/// A tiny lock guards only the ring copy (playbook: lock → copy → unlock → compute) so the FFT itself runs
/// lock-free on the caller's thread. Silence naturally yields all-zero bars, so the widget falls smoothly to a
/// flat line with no special-casing.</summary>
public sealed class SpectrumAnalyzer
{
    // 2^11 = 2048-point FFT: ~21 Hz resolution at 44.1 kHz, cheap enough to run every render frame.
    private const int FftM = 11;
    private const int FftSize = 1 << FftM;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private int _writePos;
    private bool _hasSignal;                 // any non-zero sample seen since last read (idle detection)

    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];   // precomputed Hann window
    private readonly float[] _scratch = new float[FftSize];

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    /// <summary>True while the most recent captured window carried actual sound (not pure digital silence).</summary>
    public bool HasSignal { get { lock (_gate) return _hasSignal; } }

    /// <summary>Append mono samples (range ~[-1,1]) to the ring buffer. Called on the WASAPI capture thread.</summary>
    public void AddSamples(float[] mono, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                var s = mono[i];
                if (s > 0.0004f || s < -0.0004f) _hasSignal = true;
                _ring[_writePos] = s;
                _writePos = (_writePos + 1) % FftSize;
            }
        }
    }

    /// <summary>Clear the buffer (on stop) so a restart doesn't briefly show stale bars.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _writePos = 0;
            _hasSignal = false;
        }
    }

    /// <summary>Fill <paramref name="bars"/> (length = bar count) with the current spectrum, each value in [0,1].
    /// Bands are log-spaced across the audible range and log-compressed in amplitude (dB-like), which is what makes
    /// the display track music the way the ear does. <paramref name="gain"/> is the user sensitivity multiplier.</summary>
    public void FillBars(float[] bars, float gain)
    {
        SnapshotOrdered();
        for (int i = 0; i < FftSize; i++)
        {
            _fft[i].X = _scratch[i] * _window[i];
            _fft[i].Y = 0f;
        }
        FastFourierTransform.FFT(true, FftM, _fft);

        int bins = FftSize / 2;
        int barCount = bars.Length;
        // Log-spaced bin edges from ~40 Hz up to Nyquist so bass doesn't swallow the whole display.
        const double minBin = 2;
        double maxBin = bins - 1;
        double logMin = Math.Log(minBin), logMax = Math.Log(maxBin);

        for (int b = 0; b < barCount; b++)
        {
            int lo = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * b / barCount));
            int hi = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * (b + 1) / barCount));
            lo = Math.Clamp(lo, 1, bins - 1);
            hi = Math.Clamp(Math.Max(hi, lo + 1), lo + 1, bins);

            float peak = 0f;
            for (int k = lo; k < hi; k++)
            {
                // NAudio's forward FFT already scales by 1/N, so the bin magnitude is the component amplitude —
                // do NOT normalise by N again here (that double-scaling crushed the whole spectrum to silence).
                float re = _fft[k].X, im = _fft[k].Y;
                float mag = MathF.Sqrt(re * re + im * im);
                if (mag > peak) peak = mag;
            }

            // Amplitude → dB → 0..1 over a −55..−10 dB window (tuned so music fills the display without clipping flat).
            float db = 20f * MathF.Log10(peak + 1e-6f);
            float norm = (db + 55f) / 45f;
            // Gentle high-frequency tilt: highs carry less energy, lift them so the display looks balanced.
            norm *= 0.55f + 0.65f * (b / (float)barCount);
            norm *= gain;
            bars[b] = Math.Clamp(norm, 0f, 1f);
        }
    }

    /// <summary>Fill <paramref name="wave"/> with the latest waveform for the oscilloscope, each value in ~[-1,1].</summary>
    public void FillWave(float[] wave)
    {
        SnapshotOrdered();
        int n = wave.Length;
        // Take the most recent n samples (the tail of the ordered scratch), stretched to fill the buffer.
        for (int i = 0; i < n; i++)
        {
            int src = FftSize - n + i;
            if (src < 0) src = 0;
            wave[i] = _scratch[src];
        }
    }

    /// <summary>Copy the ring into <see cref="_scratch"/> in chronological order under the lock, and note whether
    /// this window still carries signal (reset the flag so idle is detected within one window).</summary>
    private void SnapshotOrdered()
    {
        lock (_gate)
        {
            int p = _writePos;
            for (int i = 0; i < FftSize; i++)
                _scratch[i] = _ring[(p + i) % FftSize];
            _hasSignal = false;
            for (int i = 0; i < FftSize; i++)
                if (_scratch[i] > 0.0004f || _scratch[i] < -0.0004f) { _hasSignal = true; break; }
        }
    }
}
