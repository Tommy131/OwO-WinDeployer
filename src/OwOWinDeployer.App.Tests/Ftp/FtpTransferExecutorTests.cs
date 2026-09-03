using System.Collections.Concurrent;
using OwOWinDeployer.App.Services.Ftp;
using Xunit;

namespace OwOWinDeployer.App.Tests.Ftp;

public sealed class FtpTransferExecutorTests
{
    [Fact]
    public async Task LargeDownloadUsesConcurrentRangesAndPublishesOnlyCompleteFile()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 1024).Select(x => (byte)(x % 251)).ToArray();
        var factory = new MemorySessionFactory(content);
        var executor = CreateExecutor(factory, threshold: 256);
        var destination = Path.Combine(temp.Path, "result.bin");

        await executor.ExecuteAsync(Download(destination, content.Length), new Progress<long>(), CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.Equal(4, factory.Ranges.Count);
        Assert.True(factory.MaxActiveRanges >= 2);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.owo-part-*"));
    }

    [Fact]
    public async Task UnsupportedRangesFallBackToOneFullDownload()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 1024).Select(x => (byte)(x % 239)).ToArray();
        var factory = new MemorySessionFactory(content) { SupportsRanges = false };
        var executor = CreateExecutor(factory, threshold: 256);
        var destination = Path.Combine(temp.Path, "fallback.bin");

        await executor.ExecuteAsync(Download(destination, content.Length), new Progress<long>(), CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, factory.FullDownloads);
        Assert.NotEmpty(factory.Ranges);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.owo-part-*"));
    }

    [Fact]
    public async Task ConnectionLimitedServerFallsBackToOneFullDownload()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 1024).Select(x => (byte)(x % 233)).ToArray();
        var factory = new MemorySessionFactory(content) { MaxConcurrentRanges = 1 };
        using var executor = CreateExecutor(factory, threshold: 256);
        var destination = Path.Combine(temp.Path, "limited.bin");

        await executor.ExecuteAsync(Download(destination, content.Length), new Progress<long>(), CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1, factory.FullDownloads);
    }

    [Fact]
    public async Task CanceledDownloadKeepsExistingDestinationAndRemovesTemporaryFile()
    {
        using var temp = new TempDirectory();
        var original = new byte[] { 7, 8, 9 };
        var content = new byte[1024];
        var factory = new MemorySessionFactory(content) { BlockDownloads = true };
        var executor = CreateExecutor(factory, threshold: 256);
        var destination = Path.Combine(temp.Path, "existing.bin");
        await File.WriteAllBytesAsync(destination, original);
        using var cancellation = new CancellationTokenSource();

        var running = executor.ExecuteAsync(Download(destination, content.Length), new Progress<long>(), cancellation.Token);
        await factory.FirstTransferStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.owo-part-*"));
    }

    [Fact]
    public async Task CanceledUploadRemovesPartialRemoteFile()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory();
        using var executor = CreateExecutor(factory, threshold: 256);
        using var cancellation = new CancellationTokenSource();
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/upload.bin", 1024, Guid.NewGuid());

        var running = executor.ExecuteAsync(request, new Progress<long>(), cancellation.Token);
        await factory.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(factory.PartialRemoteFileExists);
        Assert.Equal(1, factory.DeleteCalls);
    }

    [Fact]
    public async Task CanceledUploadReportsResidualPathWhenCleanupFails()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory { FailDelete = true };
        using var executor = CreateExecutor(factory, threshold: 256);
        using var cancellation = new CancellationTokenSource();
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/residual.bin", 1024, Guid.NewGuid());

        var running = executor.ExecuteAsync(request, new Progress<long>(), cancellation.Token);
        await factory.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<FtpUploadCleanupException>(() => running);
        Assert.StartsWith("/remote/.residual.bin.owo-upload-", error.RemotePath, StringComparison.Ordinal);
        Assert.True(factory.PartialRemoteFileExists);
    }

    [Fact]
    public async Task CanceledUploadCleansUpWhenTransportReportsIoFailure()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory { ThrowIoOnCancel = true };
        using var executor = CreateExecutor(factory, threshold: 256);
        using var cancellation = new CancellationTokenSource();
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/upload.bin", 1024, Guid.NewGuid());

        var running = executor.ExecuteAsync(request, new Progress<long>(), cancellation.Token);
        await factory.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(factory.PartialRemoteFileExists);
        Assert.Equal(1, factory.DeleteCalls);
    }

    [Fact]
    public async Task UploadPublishesThroughTemporaryRemotePath()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory { BlockUpload = false };
        using var executor = CreateExecutor(factory, threshold: 256);
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/upload.bin", 1024, Guid.NewGuid());

        await executor.ExecuteAsync(request, new Progress<long>(), CancellationToken.None);

        Assert.StartsWith("/remote/.upload.bin.owo-upload-", factory.LastUploadPath, StringComparison.Ordinal);
        Assert.Equal((factory.LastUploadPath, "/remote/upload.bin"), factory.LastRename);
    }

    [Fact]
    public async Task CanceledUploadNeverDeletesExistingDestination()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory();
        using var executor = CreateExecutor(factory, threshold: 256);
        using var cancellation = new CancellationTokenSource();
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/upload.bin", 1024, Guid.NewGuid());

        var running = executor.ExecuteAsync(request, new Progress<long>(), cancellation.Token);
        await factory.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.DoesNotContain("/remote/upload.bin", factory.DeletedPaths);
    }

    [Fact]
    public async Task CanceledUploadCleanupTimesOut()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "upload.bin");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var factory = new UploadCleanupSessionFactory { BlockDelete = true };
        using var executor = new FtpTransferExecutor(
            new FtpConnectionSnapshot("localhost", 21, "none", "user", "password", "test"),
            factory,
            largeFileThreshold: 256,
            maxSegments: 4,
            maxConnections: 8,
            cleanupTimeout: TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var request = new FtpTransferRequest(FtpTransferDirection.Upload, source,
            "/remote/upload.bin", 1024, Guid.NewGuid());

        var running = executor.ExecuteAsync(request, new Progress<long>(), cancellation.Token);
        await factory.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAsync<FtpUploadCleanupException>(() => running)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static FtpTransferExecutor CreateExecutor(IFtpTransferSessionFactory factory, long threshold)
        => new(new FtpConnectionSnapshot("localhost", 21, "none", "user", "password", "test"),
            factory, largeFileThreshold: threshold, maxSegments: 4, maxConnections: 8);

    private static FtpTransferRequest Download(string destination, long size) => new(
        FtpTransferDirection.Download, "/remote/source.bin", destination, size, Guid.NewGuid());

    private sealed class MemorySessionFactory : IFtpTransferSessionFactory
    {
        private readonly byte[] _content;
        private int _activeRanges;
        private int _maxActiveRanges;
        private int _fullDownloads;

        public MemorySessionFactory(byte[] content) => _content = content;

        public bool SupportsRanges { get; init; } = true;
        public bool BlockDownloads { get; init; }
        public int MaxConcurrentRanges { get; init; } = int.MaxValue;
        public ConcurrentBag<(long Offset, long Length)> Ranges { get; } = new();
        public int MaxActiveRanges => Volatile.Read(ref _maxActiveRanges);
        public int FullDownloads => Volatile.Read(ref _fullDownloads);
        public TaskCompletionSource FirstTransferStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IFtpTransferSession> ConnectAsync(FtpConnectionSnapshot connection, CancellationToken cancellationToken)
            => Task.FromResult<IFtpTransferSession>(new MemorySession(this));

        private sealed class MemorySession : IFtpTransferSession
        {
            private readonly MemorySessionFactory _owner;

            public MemorySession(MemorySessionFactory owner) => _owner = owner;

            public async Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _owner._fullDownloads);
                _owner.FirstTransferStarted.TrySetResult();
                if (_owner.BlockDownloads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                await File.WriteAllBytesAsync(localPath, _owner._content, cancellationToken);
                progress?.Report(_owner._content.Length);
            }

            public async Task DownloadRangeAsync(string remotePath, string localPath, long offset, long length,
                IProgress<long>? progress, CancellationToken cancellationToken)
            {
                _owner.Ranges.Add((offset, length));
                if (!_owner.SupportsRanges) throw new FtpRangeNotSupportedException("REST rejected");
                var active = Interlocked.Increment(ref _owner._activeRanges);
                UpdateMax(ref _owner._maxActiveRanges, active);
                _owner.FirstTransferStarted.TrySetResult();
                try
                {
                    if (active > _owner.MaxConcurrentRanges)
                        throw new IOException("421 too many connections");
                    if (_owner.BlockDownloads) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    await Task.Delay(20, cancellationToken);
                    await using var output = new FileStream(localPath, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    output.Position = offset;
                    await output.WriteAsync(_owner._content.AsMemory((int)offset, (int)length), cancellationToken);
                    progress?.Report(length);
                }
                finally
                {
                    Interlocked.Decrement(ref _owner._activeRanges);
                }
            }

            public Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) => Task.CompletedTask;

            public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public void Dispose() { }
        }

        private static void UpdateMax(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current) return;
            }
        }
    }

    private sealed class UploadCleanupSessionFactory : IFtpTransferSessionFactory
    {
        private int _partialRemoteFileExists;
        private int _deleteCalls;

        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailDelete { get; init; }
        public bool ThrowIoOnCancel { get; init; }
        public bool BlockUpload { get; init; } = true;
        public bool BlockDelete { get; init; }
        public bool PartialRemoteFileExists => Volatile.Read(ref _partialRemoteFileExists) != 0;
        public int DeleteCalls => Volatile.Read(ref _deleteCalls);
        public string LastUploadPath { get; private set; } = "";
        public (string Source, string Destination) LastRename { get; private set; }
        public ConcurrentBag<string> DeletedPaths { get; } = new();

        public Task<IFtpTransferSession> ConnectAsync(
            FtpConnectionSnapshot connection,
            CancellationToken cancellationToken)
            => Task.FromResult<IFtpTransferSession>(new UploadCleanupSession(this));

        private sealed class UploadCleanupSession : IFtpTransferSession
        {
            private readonly UploadCleanupSessionFactory _owner;

            public UploadCleanupSession(UploadCleanupSessionFactory owner) => _owner = owner;

            public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task DownloadRangeAsync(string remotePath, string localPath, long offset, long length,
                IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

            public async Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress,
                CancellationToken cancellationToken)
            {
                _owner.LastUploadPath = remotePath;
                Volatile.Write(ref _owner._partialRemoteFileExists, 1);
                _owner.UploadStarted.TrySetResult();
                if (!_owner.BlockUpload)
                {
                    progress?.Report(new FileInfo(localPath).Length);
                    return;
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (_owner.ThrowIoOnCancel)
                {
                    throw new IOException("transport aborted");
                }
            }

            public Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken)
                => Task.CompletedTask;

            public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _owner._deleteCalls);
                _owner.DeletedPaths.Add(remotePath);
                if (_owner.FailDelete) throw new IOException("planned cleanup failure");
                if (_owner.BlockDelete) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                Volatile.Write(ref _owner._partialRemoteFileExists, 0);
            }

            public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
            {
                _owner.LastRename = (sourcePath, destinationPath);
                Volatile.Write(ref _owner._partialRemoteFileExists, 0);
                return Task.CompletedTask;
            }

            public void Dispose() { }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OwOFtpTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
