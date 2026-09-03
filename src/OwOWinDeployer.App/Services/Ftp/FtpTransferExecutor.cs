using System.IO;

namespace OwOWinDeployer.App.Services.Ftp;

/// <summary>Executes one physical file transfer using isolated FTP sessions and atomic local downloads.</summary>
public sealed class FtpTransferExecutor : IFtpTransferExecutor, IDisposable
{
    private readonly FtpConnectionSnapshot _connection;
    private readonly IFtpTransferSessionFactory _sessions;
    private readonly long _largeFileThreshold;
    private readonly int _maxSegments;
    private readonly SemaphoreSlim _connectionSlots;
    private readonly TimeSpan _cleanupTimeout;

    public FtpTransferExecutor(
        FtpConnectionSnapshot connection,
        IFtpTransferSessionFactory sessions,
        long largeFileThreshold = 64L * 1024 * 1024,
        int maxSegments = 4,
        int maxConnections = 8,
        TimeSpan? cleanupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sessions);
        if (largeFileThreshold < 1) throw new ArgumentOutOfRangeException(nameof(largeFileThreshold));
        if (maxSegments is < 2 or > 8) throw new ArgumentOutOfRangeException(nameof(maxSegments));
        if (maxConnections is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (cleanupTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));

        _connection = connection;
        _sessions = sessions;
        _largeFileThreshold = largeFileThreshold;
        _maxSegments = maxSegments;
        _connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(3);
    }

    public Task ExecuteAsync(FtpTransferRequest request, IProgress<long> progress, CancellationToken cancellationToken)
        => request.Kind == FtpTransferEntryKind.Directory
            ? EnsureDirectoryAsync(request, cancellationToken)
            : request.Direction == FtpTransferDirection.Download
            ? DownloadAsync(request, progress, cancellationToken)
            : UploadAsync(request, progress, cancellationToken);

    private async Task EnsureDirectoryAsync(FtpTransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Direction == FtpTransferDirection.Download)
        {
            Directory.CreateDirectory(request.DestinationPath);
            return;
        }

        using var slot = await AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var session = await _sessions.ConnectAsync(_connection, cancellationToken).ConfigureAwait(false);
        await session.EnsureDirectoryAsync(request.DestinationPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadAsync(FtpTransferRequest request, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var destinationDir = Path.GetDirectoryName(request.DestinationPath);
        if (!string.IsNullOrEmpty(destinationDir)) Directory.CreateDirectory(destinationDir);
        var temporaryPath = request.DestinationPath + ".owo-part-" + Guid.NewGuid().ToString("N");

        try
        {
            var segmented = request.Size >= _largeFileThreshold;
            if (segmented)
            {
                try
                {
                    await DownloadSegmentedAsync(request, temporaryPath, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    TryDelete(temporaryPath);
                    progress.Report(0);
                    await DownloadSingleAsync(request.SourcePath, temporaryPath, progress, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await DownloadSingleAsync(request.SourcePath, temporaryPath, progress, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var actualSize = new FileInfo(temporaryPath).Length;
            if (request.Size > 0 && actualSize != request.Size)
                throw new IOException($"Downloaded size mismatch: expected {request.Size}, got {actualSize}.");

            File.Move(temporaryPath, request.DestinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task DownloadSingleAsync(
        string remotePath, string temporaryPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        using var slot = await AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var session = await _sessions.ConnectAsync(_connection, cancellationToken).ConfigureAwait(false);
        long completed = 0;
        var aggregate = new FtpInlineProgress<long>(delta => progress.Report(Interlocked.Add(ref completed, delta)));
        await session.DownloadAsync(remotePath, temporaryPath, aggregate, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadSegmentedAsync(
        FtpTransferRequest request, string temporaryPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var segmentCount = Math.Min(_maxSegments,
            Math.Max(2, (int)Math.Ceiling(request.Size / (double)_largeFileThreshold)));
        await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                         FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            output.SetLength(request.Size);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        using var segmentedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long completed = 0;
        var baseLength = request.Size / segmentCount;
        var tasks = new List<Task>(segmentCount);
        for (var index = 0; index < segmentCount; index++)
        {
            var offset = index * baseLength;
            var length = index == segmentCount - 1 ? request.Size - offset : baseLength;
            tasks.Add(DownloadSegmentAsync(request.SourcePath, temporaryPath, offset, length,
                delta => progress.Report(Interlocked.Add(ref completed, delta)), segmentedCancellation));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            segmentedCancellation.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            var rangeFailure = tasks.Select(x => x.Exception)
                .Where(x => x != null)
                .SelectMany(x => x!.Flatten().InnerExceptions)
                .OfType<FtpRangeNotSupportedException>()
                .FirstOrDefault();
            if (rangeFailure != null) throw rangeFailure;
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private async Task DownloadSegmentAsync(
        string remotePath,
        string temporaryPath,
        long offset,
        long length,
        Action<long> report,
        CancellationTokenSource cancellation)
    {
        try
        {
            using var slot = await AcquireConnectionAsync(cancellation.Token).ConfigureAwait(false);
            using var session = await _sessions.ConnectAsync(_connection, cancellation.Token).ConfigureAwait(false);
            var segmentProgress = new FtpInlineProgress<long>(report);
            await session.DownloadRangeAsync(remotePath, temporaryPath, offset, length,
                segmentProgress, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private async Task UploadAsync(FtpTransferRequest request, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var temporaryRemotePath = TemporaryRemotePath(request.DestinationPath);
        try
        {
            using var slot = await AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var session = await _sessions.ConnectAsync(_connection, cancellationToken).ConfigureAwait(false);
            var parent = RemoteParent(request.DestinationPath);
            if (parent.Length > 0) await session.EnsureDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
            long completed = 0;
            var aggregate = new FtpInlineProgress<long>(delta => progress.Report(Interlocked.Add(ref completed, delta)));
            await session.UploadAsync(request.SourcePath, temporaryRemotePath, aggregate, cancellationToken).ConfigureAwait(false);
            await session.RenameAsync(temporaryRemotePath, request.DestinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transferError) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(_cleanupTimeout);
                var cleanupToken = cleanupCancellation.Token;
                using var slot = await AcquireConnectionAsync(cleanupToken).ConfigureAwait(false);
                using var cleanupSession = await _sessions.ConnectAsync(_connection, cleanupToken).ConfigureAwait(false);
                await cleanupSession.DeleteAsync(temporaryRemotePath, cleanupToken).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new FtpUploadCleanupException(temporaryRemotePath, cleanupError);
            }
            throw new OperationCanceledException("Upload was canceled.", transferError, cancellationToken);
        }
    }

    private async Task<IDisposable> AcquireConnectionAsync(CancellationToken cancellationToken)
    {
        await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreLease(_connectionSlots);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string RemoteParent(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : slash == 0 ? "/" : normalized[..slash];
    }

    private static string TemporaryRemotePath(string destinationPath)
    {
        var normalized = destinationPath.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var parent = slash < 0 ? "" : normalized[..(slash + 1)];
        var name = slash < 0 ? normalized : normalized[(slash + 1)..];
        return $"{parent}.{name}.owo-upload-{Guid.NewGuid():N}";
    }

    public void Dispose() => _connectionSlots.Dispose();

    private sealed class SemaphoreLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public SemaphoreLease(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
