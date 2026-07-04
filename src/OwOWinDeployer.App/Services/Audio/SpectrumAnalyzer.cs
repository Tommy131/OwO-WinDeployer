using NAudio.Dsp;

namespace OwOWinDeployer.App.Services.Audio;

/// <summary>Turns a rolling window of mono samples into everything the visualizer needs: a normalised frequency
/// spectrum (bars/radial/spectrogram), the raw waveform (oscilloscope/ribbon), split bass/mid/treble levels, an
/// overall loudness, a beat pulse + estimated BPM, and the dominant frequency (→ musical note). The capture thread
/// pushes samples via <see cref="AddSamples"/>; the UI thread calls <see cref="Update"/> once per frame (runs the
/// single FFT + analysis) and then reads the results / fills buffers.
///
/// A tiny lock guards only the ring copy (playbook: lock → copy → unlock → compute) so the FFT runs lock-free.</summary>
public sealed class SpectrumAnalyzer
{
    // 2^11 = 2048-point FFT: ~21 Hz resolution at 44.1 kHz, cheap enough to run every render frame.
    private const int FftM = 11;
    private const int FftSize = 1 << FftM;
    private const int Bins = FftSize / 2;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _ringL = new float[FftSize];   // left channel (dual-channel style)
    private readonly float[] _ringR = new float[FftSize];   // right channel
    private int _writePos;
    private bool _hasSignal;

    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];   // precomputed Hann window
    private readonly float[] _scratch = new float[FftSize];
    private readonly float[] _mag = new float[Bins];         // magnitude per bin (from the last Update)

    /// <summary>Output sample rate of the captured device — needed to map FFT bins to Hz. Set by the capture service.</summary>
    public int SampleRate { get; set; } = 48000;

    // ── per-frame analysis outputs (read after Update) ──────────────────────────
    /// <summary>Split band levels 0..1 (low ~20–250 Hz, mid ~250–2 kHz, high ~2–16 kHz).</summary>
    public float Bass { get; private set; }
    public float Mid { get; private set; }
    public float Treble { get; private set; }
    /// <summary>Overall loudness 0..1 (RMS, dB-mapped).</summary>
    public float Level { get; private set; }
    /// <summary>Beat pulse 0..1 — spikes to 1 on a detected kick and decays smoothly.</summary>
    public float BeatPulse { get; private set; }
    /// <summary>Estimated tempo in BPM (0 until enough beats are seen); ~60–200.</summary>
    public int Bpm { get; private set; }
    /// <summary>Dominant frequency in Hz (the loudest spectral peak), or 0 when quiet.</summary>
    public float DominantHz { get; private set; }

    // ── beat detection state ──
    private const int EnergyHist = 43;                       // ~0.7 s of history at 60 fps
    private readonly float[] _energy = new float[EnergyHist];
    private int _energyPos;
    private long _lastBeatMs;
    private readonly long[] _intervals = new long[8];
    private int _intervalPos;

    public SpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1))));
    }

    public bool HasSignal { get { lock (_gate) return _hasSignal; } }

    /// <summary>Append samples (range ~[-1,1]) to the ring buffers. <paramref name="left"/>/<paramref name="right"/>
    /// are optional (mono source → they mirror <paramref name="mono"/>). Called on the WASAPI capture thread.</summary>
    public void AddSamples(float[] mono, float[]? left, float[]? right, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                var s = mono[i];
                if (s > 0.0004f || s < -0.0004f) _hasSignal = true;
                _ring[_writePos] = s;
                _ringL[_writePos] = left != null ? left[i] : s;
                _ringR[_writePos] = right != null ? right[i] : s;
                _writePos = (_writePos + 1) % FftSize;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring); Array.Clear(_ringL); Array.Clear(_ringR);
            _writePos = 0;
            _hasSignal = false;
        }
        Array.Clear(_mag); Array.Clear(_energy);
        Bass = Mid = Treble = Level = BeatPulse = DominantHz = 0; Bpm = 0;
    }

    /// <summary>Run the once-per-frame analysis: snapshot the ring, FFT it, and update magnitudes, band levels,
    /// loudness, the beat pulse/BPM and the dominant frequency. Call this before <see cref="FillBars"/> /
    /// <see cref="FillWave"/> and before reading any of the level/beat properties.</summary>
    public void Update(long nowMs)
    {
        SnapshotOrdered(out float rms);
        for (int i = 0; i < FftSize; i++) { _fft[i].X = _scratch[i] * _window[i]; _fft[i].Y = 0f; }
        FastFourierTransform.FFT(true, FftM, _fft);
        for (int k = 0; k < Bins; k++)
            _mag[k] = MathF.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);

        // Band levels (dB-mapped like the bars).
        Bass = BandLevel(20, 250);
        Mid = BandLevel(250, 2000);
        Treble = BandLevel(2000, 16000);
        Level = Norm(20f * MathF.Log10(rms + 1e-6f), -50f, 0f);

        // Dominant peak → Hz.
        int peakBin = 1; float peakMag = 0;
        for (int k = 1; k < Bins; k++) if (_mag[k] > peakMag) { peakMag = _mag[k]; peakBin = k; }
        DominantHz = peakMag > 1e-4f ? peakBin * (float)SampleRate / FftSize : 0f;

        DetectBeat(nowMs);
    }

    /// <summary>Energy-based kick detection: an instantaneous bass-energy spike well above its short-term average
    /// (Patin's variance-adaptive threshold) registers a beat; consecutive beat intervals give a BPM estimate.</summary>
    private void DetectBeat(long nowMs)
    {
        // Instant low-frequency energy (sum of squared magnitudes up to ~200 Hz).
        int hiBin = Math.Clamp((int)(200f * FftSize / SampleRate), 2, Bins - 1);
        float e = 0; for (int k = 1; k <= hiBin; k++) e += _mag[k] * _mag[k];

        float avg = 0; for (int i = 0; i < EnergyHist; i++) avg += _energy[i]; avg /= EnergyHist;
        float var = 0; for (int i = 0; i < EnergyHist; i++) { var d = _energy[i] - avg; var += d * d; } var /= EnergyHist;
        float c = -0.0025714f * var / (avg * avg + 1e-9f) + 1.55f;   // adaptive sensitivity
        c = Math.Clamp(c, 1.15f, 2.2f);

        _energy[_energyPos] = e; _energyPos = (_energyPos + 1) % EnergyHist;

        bool beat = e > c * avg && e > 1e-5f && nowMs - _lastBeatMs > 250;   // ≤240 BPM guard
        if (beat)
        {
            if (_lastBeatMs > 0)
            {
                _intervals[_intervalPos] = nowMs - _lastBeatMs;
                _intervalPos = (_intervalPos + 1) % _intervals.Length;
                EstimateBpm();
            }
            _lastBeatMs = nowMs;
            BeatPulse = 1f;
        }
        else BeatPulse *= 0.86f;   // smooth decay
        if (BeatPulse < 0.001f) BeatPulse = 0f;
    }

    private void EstimateBpm()
    {
        // Median of the recent beat intervals → BPM (robust to the odd missed/extra beat).
        var vals = _intervals.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (vals.Length < 3) return;
        long med = vals[vals.Length / 2];
        if (med <= 0) return;
        int bpm = (int)Math.Round(60000.0 / med);
        // Fold into a musical range.
        while (bpm > 200) bpm /= 2;
        while (bpm > 0 && bpm < 60) bpm *= 2;
        if (bpm is >= 60 and <= 200) Bpm = bpm;
    }

    /// <summary>Fill <paramref name="bars"/> from the magnitudes computed by the last <see cref="Update"/>.</summary>
    public void FillBars(float[] bars, float gain)
    {
        int barCount = bars.Length;
        const double minBin = 2;
        double maxBin = Bins - 1;
        double logMin = Math.Log(minBin), logMax = Math.Log(maxBin);

        for (int b = 0; b < barCount; b++)
        {
            int lo = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * b / barCount));
            int hi = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * (b + 1) / barCount));
            lo = Math.Clamp(lo, 1, Bins - 1);
            hi = Math.Clamp(Math.Max(hi, lo + 1), lo + 1, Bins);

            float peak = 0f;
            for (int k = lo; k < hi; k++) if (_mag[k] > peak) peak = _mag[k];

            float db = 20f * MathF.Log10(peak + 1e-6f);
            float norm = (db + 55f) / 45f;
            norm *= 0.55f + 0.65f * (b / (float)barCount);   // high-frequency tilt
            norm *= gain;
            bars[b] = Math.Clamp(norm, 0f, 1f);
        }
    }

    /// <summary>Fill <paramref name="wave"/> with the latest waveform (from the last <see cref="Update"/> snapshot).</summary>
    public void FillWave(float[] wave)
    {
        int n = wave.Length;
        for (int i = 0; i < n; i++)
        {
            int src = FftSize - n + i;
            if (src < 0) src = 0;
            wave[i] = _scratch[src];
        }
    }

    /// <summary>Fill the latest left/right waveforms (for the dual-channel style). Snapshots the L/R rings in order
    /// under the lock, then copies the most recent samples.</summary>
    public void FillWaveStereo(float[] left, float[] right)
    {
        int n = Math.Min(left.Length, right.Length);
        lock (_gate)
        {
            int p = _writePos;
            for (int i = 0; i < n; i++)
            {
                int src = (p + FftSize - n + i) % FftSize;
                left[i] = _ringL[src];
                right[i] = _ringR[src];
            }
        }
    }

    /// <summary>The normalised (0..1) magnitudes over the whole spectrum for the spectrogram, dB-mapped. Length =
    /// <paramref name="dest"/>.Length, log-spaced like the bars.</summary>
    public void FillSpectrum(float[] dest, float gain)
    {
        int n = dest.Length;
        double logMin = Math.Log(2), logMax = Math.Log(Bins - 1);
        for (int b = 0; b < n; b++)
        {
            int lo = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * b / n));
            int hi = (int)Math.Floor(Math.Exp(logMin + (logMax - logMin) * (b + 1) / n));
            lo = Math.Clamp(lo, 1, Bins - 1); hi = Math.Clamp(Math.Max(hi, lo + 1), lo + 1, Bins);
            float peak = 0f; for (int k = lo; k < hi; k++) if (_mag[k] > peak) peak = _mag[k];
            dest[b] = Math.Clamp((20f * MathF.Log10(peak + 1e-6f) + 55f) / 45f * gain, 0f, 1f);
        }
    }

    private float BandLevel(float loHz, float hiHz)
    {
        int lo = Math.Clamp((int)(loHz * FftSize / SampleRate), 1, Bins - 1);
        int hi = Math.Clamp((int)(hiHz * FftSize / SampleRate), lo + 1, Bins);
        float peak = 0f; for (int k = lo; k < hi; k++) if (_mag[k] > peak) peak = _mag[k];
        return Norm(20f * MathF.Log10(peak + 1e-6f), -55f, -10f);
    }

    private static float Norm(float v, float lo, float hi) => Math.Clamp((v - lo) / (hi - lo), 0f, 1f);

    private void SnapshotOrdered(out float rms)
    {
        double sumSq = 0;
        lock (_gate)
        {
            int p = _writePos;
            for (int i = 0; i < FftSize; i++) _scratch[i] = _ring[(p + i) % FftSize];
            _hasSignal = false;
            for (int i = 0; i < FftSize; i++)
                if (_scratch[i] > 0.0004f || _scratch[i] < -0.0004f) { _hasSignal = true; break; }
        }
        for (int i = 0; i < FftSize; i++) sumSq += (double)_scratch[i] * _scratch[i];
        rms = (float)Math.Sqrt(sumSq / FftSize);
    }
}
