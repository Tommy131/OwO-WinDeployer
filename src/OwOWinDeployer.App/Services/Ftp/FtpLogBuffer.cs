namespace OwOWinDeployer.App.Services.Ftp;

/// <summary>Bounds protocol history and coalesces high-frequency producer updates into UI snapshots.</summary>
public sealed class FtpLogBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;
    private bool _dirty;

    public FtpLogBuffer(int capacity = 500)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity) _lines.Dequeue();
            _dirty = true;
        }
    }

    public bool TryTakeSnapshot(out string text)
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                text = "";
                return false;
            }
            text = string.Join("\n", _lines);
            _dirty = false;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _dirty = false;
        }
    }
}
