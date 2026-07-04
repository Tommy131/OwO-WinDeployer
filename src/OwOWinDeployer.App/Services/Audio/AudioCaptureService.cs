using NAudio.CoreAudioApi;
using NAudio.Wave;
using OwOWinDeployer.App.Services.Infra;

namespace OwOWinDeployer.App.Services.Audio;

/// <summary>Captures the system's audio <b>output</b> (WASAPI loopback of the default render device) and feeds mono
/// samples to a <see cref="SpectrumAnalyzer"/>. Capture only runs while the widget is visible, so there is no
/// background listening. Everything is best-effort and wrapped: a machine with no audio device, or a device that
/// disappears, must degrade to a flat line — never crash the app.</summary>
public sealed class AudioCaptureService : IDisposable
{
    private readonly SpectrumAnalyzer _analyzer;
    private WasapiLoopbackCapture? _capture;
    private float[] _mono = new float[4096];   // reused scratch, grown as needed
    private float[] _left = new float[4096];
    private float[] _right = new float[4096];
    private bool _running;                      // caller intends capture to be on (survives device-switch restarts)
    private bool _restarting;

    public AudioCaptureService(SpectrumAnalyzer analyzer) => _analyzer = analyzer;

    /// <summary>True once loopback capture is actually running (a device was found and started).</summary>
    public bool IsRunning => _capture != null;

    public void Start()
    {
        _running = true;
        TryOpen();
    }

    public void Stop()
    {
        _running = false;
        Close();
        _analyzer.Reset();
    }

    private void TryOpen()
    {
        if (_capture != null) return;
        try
        {
            var cap = new WasapiLoopbackCapture();   // default render device
            _analyzer.SampleRate = cap.WaveFormat.SampleRate;   // map FFT bins → Hz correctly
            cap.DataAvailable += OnData;
            cap.RecordingStopped += OnStopped;
            _capture = cap;
            cap.StartRecording();
        }
        catch (Exception ex)
        {
            // No output device / driver blocked loopback: leave _capture null → widget shows an idle line.
            AuditLog.Action($"音频波形组件：无法启动音频采集（{ex.Message}）");
            Close();
        }
    }

    private void Close()
    {
        var cap = _capture;
        _capture = null;
        if (cap == null) return;
        try
        {
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnStopped;
            cap.StopRecording();
            cap.Dispose();
        }
        catch { /* already gone */ }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var wf = _capture?.WaveFormat;
            if (wf == null || e.BytesRecorded <= 0) return;

            int channels = Math.Max(1, wf.Channels);
            int bytesPerSample = Math.Max(1, wf.BitsPerSample / 8);
            int frameSize = bytesPerSample * channels;
            int frames = e.BytesRecorded / frameSize;
            if (frames <= 0) return;
            if (_mono.Length < frames) { _mono = new float[frames]; _left = new float[frames]; _right = new float[frames]; }

            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                || (wf.Encoding == WaveFormatEncoding.Extensible && bytesPerSample == 4);

            for (int f = 0; f < frames; f++)
            {
                int baseOff = f * frameSize;
                float sum = 0f, first = 0f, second = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int off = baseOff + c * bytesPerSample;
                    float v;
                    if (isFloat) v = BitConverter.ToSingle(e.Buffer, off);
                    else if (bytesPerSample == 2) v = BitConverter.ToInt16(e.Buffer, off) / 32768f;
                    else v = 0f;
                    sum += v;
                    if (c == 0) first = v; else if (c == 1) second = v;
                }
                _mono[f] = sum / channels;
                _left[f] = first;
                _right[f] = channels > 1 ? second : first;
            }
            _analyzer.AddSamples(_mono, _left, _right, frames);
        }
        catch { /* transient buffer/format hiccup — drop this chunk, keep running */ }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // If the caller still wants capture and this stop was unexpected (e.g. the default device switched),
        // rebuild once against the new default device. The _restarting guard avoids a tight loop.
        if (!_running || _restarting) { Close(); return; }
        _restarting = true;
        Close();
        try { TryOpen(); }
        finally { _restarting = false; }
    }

    public void Dispose() => Stop();
}
