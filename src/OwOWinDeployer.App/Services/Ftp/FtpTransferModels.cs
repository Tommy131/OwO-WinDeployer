using System.IO;

namespace OwOWinDeployer.App.Services.Ftp;

public enum FtpTransferDirection
{
    Download,
    Upload,
}

public enum FtpTransferEntryKind
{
    File,
    Directory,
}

public enum FtpTransferState
{
    Scanning,
    Queued,
    Connecting,
    Transferring,
    Completing,
    Completed,
    Canceling,
    Canceled,
    Failed,
}

public sealed record FtpTransferRequest(
    FtpTransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    long Size,
    Guid BatchId,
    FtpTransferEntryKind Kind = FtpTransferEntryKind.File);

public sealed record FtpConnectionSnapshot(
    string Host,
    int Port,
    string TlsMode,
    string UserName,
    string Password,
    string DisplayName)
{
    public override string ToString() => $"{DisplayName} ({UserName}@{Host}:{Port}, {TlsMode})";
}

public sealed class FtpTransferJob
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FtpTransferState _state = FtpTransferState.Queued;
    private long _bytesTransferred;
    private string? _error;
    private string? _residualPath;

    internal FtpTransferJob(FtpTransferRequest request)
    {
        Id = Guid.NewGuid();
        Request = request;
    }

    public Guid Id { get; }
    public FtpTransferRequest Request { get; }
    public FtpTransferState State { get { lock (_gate) return _state; } }
    public long BytesTransferred => Interlocked.Read(ref _bytesTransferred);
    public string? Error { get { lock (_gate) return _error; } }
    public string? ResidualPath { get { lock (_gate) return _residualPath; } }
    public Task Completion => _completion.Task;
    internal CancellationToken CancellationToken => _cancellation.Token;

    internal event Action<FtpTransferJob>? Changed;

    internal bool TrySetState(FtpTransferState state, string? error = null, string? residualPath = null)
    {
        lock (_gate)
        {
            if (!CanTransition(_state, state))
                return false;
            _state = state;
            _error = error;
            _residualPath = residualPath;
        }
        Changed?.Invoke(this);
        if (state is FtpTransferState.Completed or FtpTransferState.Canceled or FtpTransferState.Failed)
            _completion.TrySetResult();
        return true;
    }

    private static bool CanTransition(FtpTransferState from, FtpTransferState to)
        => from switch
        {
            FtpTransferState.Queued => to is FtpTransferState.Connecting or FtpTransferState.Canceling
                or FtpTransferState.Canceled or FtpTransferState.Failed,
            FtpTransferState.Connecting => to is FtpTransferState.Transferring or FtpTransferState.Canceling
                or FtpTransferState.Canceled or FtpTransferState.Failed,
            FtpTransferState.Transferring => to is FtpTransferState.Completing or FtpTransferState.Canceling
                or FtpTransferState.Canceled or FtpTransferState.Failed,
            FtpTransferState.Completing => to is FtpTransferState.Completed or FtpTransferState.Canceling
                or FtpTransferState.Canceled or FtpTransferState.Failed,
            FtpTransferState.Canceling => to == FtpTransferState.Canceled,
            _ => false,
        };

    internal void SetBytes(long value)
    {
        Interlocked.Exchange(ref _bytesTransferred, Math.Max(0, value));
        Changed?.Invoke(this);
    }

    internal void Cancel()
    {
        if (!TrySetState(FtpTransferState.Canceling)) return;
        _cancellation.Cancel();
    }

    internal void Dispose() => _cancellation.Dispose();
}

public interface IFtpTransferExecutor
{
    /// <summary>Progress values are cumulative bytes for the complete logical file.</summary>
    Task ExecuteAsync(FtpTransferRequest request, IProgress<long> progress, CancellationToken cancellationToken);
}

internal sealed class FtpInlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

public interface IFtpTransferSessionFactory
{
    Task<IFtpTransferSession> ConnectAsync(FtpConnectionSnapshot connection, CancellationToken cancellationToken);
}

public interface IFtpTransferSession : IDisposable
{
    Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress, CancellationToken cancellationToken);
    Task DownloadRangeAsync(string remotePath, string localPath, long offset, long length,
        IProgress<long>? progress, CancellationToken cancellationToken);
    Task UploadAsync(string localPath, string remotePath, IProgress<long>? progress, CancellationToken cancellationToken);
    Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken);
    Task DeleteAsync(string remotePath, CancellationToken cancellationToken);
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}

public interface IFtpDirectorySessionFactory
{
    Task<IFtpDirectorySession> ConnectAsync(FtpConnectionSnapshot connection, CancellationToken cancellationToken);
}

public interface IFtpDirectorySession : IDisposable
{
    Task<IReadOnlyList<FtpRemoteEntry>> ListAsync(string remotePath, CancellationToken cancellationToken);
}

public sealed class FtpRangeNotSupportedException : IOException
{
    public FtpRangeNotSupportedException(string message) : base(message) { }
}

public sealed class FtpUploadCleanupException : IOException
{
    public FtpUploadCleanupException(string remotePath, Exception innerException)
        : base($"Upload was canceled, but the partial remote file could not be removed: {remotePath}", innerException)
        => RemotePath = remotePath;

    public string RemotePath { get; }
}
