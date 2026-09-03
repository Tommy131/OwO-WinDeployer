using System.Collections.Concurrent;
using System.Threading.Channels;

namespace OwOWinDeployer.App.Services.Ftp;

public sealed class FtpTransferCoordinator : IAsyncDisposable
{
    private const int WorkerCount = 8;
    private readonly IFtpTransferExecutor _executor;
    private readonly Channel<FtpTransferJob> _queue;
    private readonly ConcurrentDictionary<Guid, FtpTransferJob> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly object _idleGate = new();
    private TaskCompletionSource _idle = CompletedSource();
    private TaskCompletionSource _concurrencyPulse = NewSource();
    private readonly TimeSpan _shutdownTimeout;
    private int _pending;
    private int _maxConcurrentFiles;
    private int _disposed;
    private int _resourcesDisposed;

    public FtpTransferCoordinator(
        IFtpTransferExecutor executor,
        int maxConcurrentFiles = 4,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        if (maxConcurrentFiles is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maxConcurrentFiles));
        if (shutdownTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));

        _executor = executor;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(3);
        _maxConcurrentFiles = maxConcurrentFiles;
        _queue = Channel.CreateUnbounded<FtpTransferJob>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
        });
        _workers = Enumerable.Range(0, WorkerCount)
            .Select(workerIndex => Task.Run(() => RunWorkerAsync(workerIndex)))
            .ToArray();
    }

    public event Action<FtpTransferJob>? JobChanged;

    public IReadOnlyList<FtpTransferJob> Jobs => _jobs.Values.ToList();
    public Task WorkersCompletion => Task.WhenAll(_workers);
    public int MaxConcurrentFiles => Volatile.Read(ref _maxConcurrentFiles);

    public void SetMaxConcurrentFiles(int value)
    {
        if (value is < 1 or > WorkerCount) throw new ArgumentOutOfRangeException(nameof(value));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _maxConcurrentFiles, value) == value) return;
        var previous = Interlocked.Exchange(ref _concurrencyPulse, NewSource());
        previous.TrySetResult();
    }

    public IReadOnlyList<FtpTransferJob> Enqueue(IEnumerable<FtpTransferRequest> requests)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var added = new List<FtpTransferJob>();
        foreach (var request in requests)
        {
            var job = new FtpTransferJob(request);
            job.Changed += OnJobChanged;
            _jobs[job.Id] = job;
            MarkPending();
            if (!_queue.Writer.TryWrite(job))
            {
                CompleteJob(job, FtpTransferState.Failed, "Transfer queue is closed.");
                continue;
            }
            added.Add(job);
            JobChanged?.Invoke(job);
        }
        return added;
    }

    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        job.Cancel();
        return true;
    }

    public bool Remove(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        if (job.State is not (FtpTransferState.Completed or FtpTransferState.Canceled or FtpTransferState.Failed))
            return false;
        if (!_jobs.TryRemove(jobId, out job)) return false;
        job.Changed -= OnJobChanged;
        job.Dispose();
        return true;
    }

    public void CancelAll()
    {
        foreach (var job in _jobs.Values) job.Cancel();
    }

    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_idleGate) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    private async Task RunWorkerAsync(int workerIndex)
    {
        try
        {
            while (true)
            {
                await WaitUntilEnabledAsync(workerIndex, _shutdown.Token).ConfigureAwait(false);
                if (!await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false)) break;
                await WaitUntilEnabledAsync(workerIndex, _shutdown.Token).ConfigureAwait(false);
                if (!_queue.Reader.TryRead(out var job)) continue;

                if (job.CancellationToken.IsCancellationRequested)
                {
                    CompleteJob(job, FtpTransferState.Canceled);
                    continue;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token, job.CancellationToken);
                try
                {
                    job.TrySetState(FtpTransferState.Connecting);
                    job.TrySetState(FtpTransferState.Transferring);
                    var progress = new FtpInlineProgress<long>(job.SetBytes);
                    await _executor.ExecuteAsync(job.Request, progress, linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    job.TrySetState(FtpTransferState.Completing);
                    CompleteJob(job, FtpTransferState.Completed);
                }
                catch (FtpUploadCleanupException ex)
                {
                    CompleteJob(job, FtpTransferState.Failed, ex.Message, ex.RemotePath);
                }
                catch (Exception) when (linked.IsCancellationRequested)
                {
                    CompleteJob(job, FtpTransferState.Canceled);
                }
                catch (Exception ex)
                {
                    CompleteJob(job, FtpTransferState.Failed, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task WaitUntilEnabledAsync(int workerIndex, CancellationToken cancellationToken)
    {
        while (workerIndex >= Volatile.Read(ref _maxConcurrentFiles))
        {
            var pulse = Volatile.Read(ref _concurrencyPulse);
            if (workerIndex < Volatile.Read(ref _maxConcurrentFiles)) return;
            await pulse.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteJob(FtpTransferJob job, FtpTransferState state, string? error = null, string? residualPath = null)
    {
        if (!job.TrySetState(state, error, residualPath)) return;
        if (Interlocked.Decrement(ref _pending) != 0) return;
        lock (_idleGate) _idle.TrySetResult();
    }

    private void MarkPending()
    {
        if (Interlocked.Increment(ref _pending) != 1) return;
        lock (_idleGate)
        {
            if (_idle.Task.IsCompleted)
                _idle = NewSource();
        }
    }

    private void OnJobChanged(FtpTransferJob job) => JobChanged?.Invoke(job);

    private static TaskCompletionSource NewSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        var source = NewSource();
        source.SetResult();
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelAll();
        _queue.Writer.TryComplete();
        Volatile.Write(ref _maxConcurrentFiles, WorkerCount);
        Volatile.Read(ref _concurrencyPulse).TrySetResult();
        var workers = Task.WhenAll(_workers);
        if (!await WaitForWorkersAsync(workers).ConfigureAwait(false))
        {
            _shutdown.Cancel();
            if (!await WaitForWorkersAsync(workers).ConfigureAwait(false))
            {
                _ = workers.ContinueWith(_ => DisposeResources(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
        }
        DisposeResources();
    }

    private async Task<bool> WaitForWorkersAsync(Task workers)
    {
        try
        {
            await workers.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _shutdown.Cancel();
        _shutdown.Dispose();
        foreach (var job in _jobs.Values)
        {
            job.Changed -= OnJobChanged;
            job.Dispose();
        }
    }
}
