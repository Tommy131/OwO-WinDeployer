using System.Collections.Concurrent;
using OwOWinDeployer.App.Services.Ftp;
using Xunit;

namespace OwOWinDeployer.App.Tests.Ftp;

public sealed class FtpTransferCoordinatorTests
{
    [Fact]
    public async Task RunsFilesInParallelWithoutExceedingConfiguredLimit()
    {
        var executor = new ControlledExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 3);
        var jobs = coordinator.Enqueue(Enumerable.Range(0, 6).Select(Request));

        await executor.WaitForStartsAsync(3, CancellationToken.None);

        Assert.Equal(3, executor.ActiveCount);
        Assert.Equal(3, executor.MaxActiveCount);
        Assert.Equal(3, jobs.Count(x => x.State == FtpTransferState.Queued));

        executor.ReleaseAll();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(jobs, x => Assert.Equal(FtpTransferState.Completed, x.State));
        Assert.Equal(3, executor.MaxActiveCount);
    }

    [Fact]
    public async Task CancelStopsOnlyTheSelectedJob()
    {
        var executor = new ControlledExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 2);
        var jobs = coordinator.Enqueue(new[] { Request(1), Request(2) });

        await executor.WaitForStartsAsync(2, CancellationToken.None);
        coordinator.Cancel(jobs[0].Id);
        executor.Release("source-2");
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FtpTransferState.Canceled, jobs[0].State);
        Assert.Equal(FtpTransferState.Completed, jobs[1].State);
    }

    [Fact]
    public async Task FailedJobDoesNotStopRemainingJobs()
    {
        var executor = new ControlledExecutor(failSource: "source-1");
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 2);
        var jobs = coordinator.Enqueue(new[] { Request(1), Request(2) });

        await executor.WaitForStartsAsync(2, CancellationToken.None);
        executor.ReleaseAll();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FtpTransferState.Failed, jobs[0].State);
        Assert.Contains("planned failure", jobs[0].Error, StringComparison.Ordinal);
        Assert.Equal(FtpTransferState.Completed, jobs[1].State);
    }

    [Fact]
    public async Task RaisingConcurrencyStartsAdditionalQueuedFiles()
    {
        var executor = new ControlledExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 1);
        coordinator.Enqueue(Enumerable.Range(0, 4).Select(Request));

        await executor.WaitForStartsAsync(1, CancellationToken.None);
        Assert.Equal(1, executor.ActiveCount);

        coordinator.SetMaxConcurrentFiles(3);
        await executor.WaitForStartsAsync(3, CancellationToken.None);

        Assert.Equal(3, executor.ActiveCount);
        executor.ReleaseAll();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, executor.MaxActiveCount);
    }

    [Fact]
    public async Task CanceledJobCannotBecomeCompletedWhenExecutorReturnsAfterCancellation()
    {
        var executor = new CancellationIgnoringExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 1);
        var job = Assert.Single(coordinator.Enqueue(new[] { Request(1) }));

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Cancel(job.Id);
        executor.Release.TrySetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FtpTransferState.Canceled, job.State);
    }

    [Fact]
    public async Task CancellationRelatedIoFailureEndsCanceled()
    {
        var executor = new CancellationIoFailureExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 1);
        var job = Assert.Single(coordinator.Enqueue(new[] { Request(1) }));

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Cancel(job.Id);
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(FtpTransferState.Canceled, job.State);
    }

    [Fact]
    public async Task DisposeReturnsWhenExecutorIgnoresCancellation()
    {
        var executor = new CancellationIgnoringExecutor();
        var coordinator = new FtpTransferCoordinator(
            executor,
            maxConcurrentFiles: 1,
            shutdownTimeout: TimeSpan.FromMilliseconds(50));
        coordinator.Enqueue(new[] { Request(1) });
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        executor.Release.TrySetResult();
    }

    [Fact]
    public async Task RemoveReleasesOnlyTerminalJobs()
    {
        var executor = new ControlledExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 1);
        var jobs = coordinator.Enqueue(new[] { Request(1), Request(2) });
        await executor.WaitForStartsAsync(1, CancellationToken.None);

        Assert.False(coordinator.Remove(jobs[0].Id));
        executor.ReleaseAll();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.Remove(jobs[0].Id));
        Assert.DoesNotContain(coordinator.Jobs, x => x.Id == jobs[0].Id);
        Assert.Contains(coordinator.Jobs, x => x.Id == jobs[1].Id);
    }

    [Fact]
    public async Task CancellationPreventsLateStateTransitions()
    {
        var executor = new CancellationIgnoringExecutor();
        await using var coordinator = new FtpTransferCoordinator(executor, maxConcurrentFiles: 1);
        var job = Assert.Single(coordinator.Enqueue(new[] { Request(1) }));

        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Cancel(job.Id);
        Assert.Equal(FtpTransferState.Canceling, job.State);

        executor.Release.TrySetResult();
        await coordinator.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FtpTransferState.Canceled, job.State);
    }

    private static FtpTransferRequest Request(int id) => new(
        FtpTransferDirection.Download,
        $"source-{id}",
        $"destination-{id}",
        1024,
        Guid.Parse("10000000-0000-0000-0000-000000000001"));

    private sealed class ControlledExecutor : IFtpTransferExecutor
    {
        private readonly string? _failSource;
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _releases = new();
        private int _starts;
        private int _active;
        private int _maxActive;
        private int _releaseEverything;

        public ControlledExecutor(string? failSource = null) => _failSource = failSource;

        public int ActiveCount => Volatile.Read(ref _active);
        public int MaxActiveCount => Volatile.Read(ref _maxActive);

        public async Task ExecuteAsync(FtpTransferRequest request, IProgress<long> progress, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMax(active);
            var release = _releases.GetOrAdd(request.SourcePath,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Volatile.Read(ref _releaseEverything) != 0) release.TrySetResult();
            Interlocked.Increment(ref _starts);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                if (request.SourcePath == _failSource) throw new IOException("planned failure");
                progress.Report(request.Size);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async Task WaitForStartsAsync(int count, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref _starts) < count)
                await Task.Delay(10, timeout.Token);
        }

        public void Release(string source) => _releases.GetOrAdd(source,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        public void ReleaseAll()
        {
            Volatile.Write(ref _releaseEverything, 1);
            foreach (var release in _releases.Values) release.TrySetResult();
        }

        private void UpdateMax(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxActive);
                if (value <= current || Interlocked.CompareExchange(ref _maxActive, value, current) == current) return;
            }
        }
    }

    private sealed class CancellationIgnoringExecutor : IFtpTransferExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            FtpTransferRequest request,
            IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            progress.Report(request.Size);
        }
    }

    private sealed class CancellationIoFailureExecutor : IFtpTransferExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(
            FtpTransferRequest request,
            IProgress<long> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new IOException("transport aborted");
            }
        }
    }
}
