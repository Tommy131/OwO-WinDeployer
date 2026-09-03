using System.IO;
using System.Runtime.CompilerServices;

namespace OwOWinDeployer.App.Services.Ftp;

/// <summary>Streams local or remote directory trees into independent physical transfer requests.</summary>
public sealed class FtpTransferPlanner
{
    private readonly IFtpDirectorySessionFactory? _directorySessions;

    public FtpTransferPlanner(IFtpDirectorySessionFactory? directorySessions = null)
        => _directorySessions = directorySessions;

    public async IAsyncEnumerable<FtpTransferRequest> PlanUploadsAsync(
        IEnumerable<string> sourcePaths,
        string remoteDirectory,
        Guid batchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (var request in PlanLocalAsync(sourcePath,
                               CombineRemote(remoteDirectory, Path.GetFileName(sourcePath.TrimEnd('\\', '/'))),
                               batchId, cancellationToken))
                yield return request;
        }
    }

    public async IAsyncEnumerable<FtpTransferRequest> PlanDownloadsAsync(
        FtpConnectionSnapshot connection,
        IEnumerable<FtpRemoteEntry> entries,
        string remoteDirectory,
        string localDirectory,
        Guid batchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_directorySessions == null)
            throw new InvalidOperationException("A directory session factory is required for remote planning.");

        using var session = await _directorySessions.ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var (entry, localName) in AllocateLocalNames(entries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remotePath = CombineRemote(remoteDirectory, entry.Name);
            var localPath = Path.Combine(localDirectory, localName);
            if (!entry.IsDir)
            {
                yield return new FtpTransferRequest(FtpTransferDirection.Download, remotePath, localPath,
                    entry.Size, batchId);
                continue;
            }

            await foreach (var request in PlanRemoteDirectoryAsync(session, remotePath, localPath,
                               batchId, cancellationToken))
                yield return request;
        }
    }

    private static async IAsyncEnumerable<FtpTransferRequest> PlanLocalAsync(
        string localPath,
        string remotePath,
        Guid batchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(localPath))
        {
            yield return new FtpTransferRequest(FtpTransferDirection.Upload, localPath, remotePath,
                new FileInfo(localPath).Length, batchId);
            yield break;
        }

        if (!Directory.Exists(localPath)) yield break;
        yield return new FtpTransferRequest(FtpTransferDirection.Upload, localPath, remotePath, 0,
            batchId, FtpTransferEntryKind.Directory);
        await Task.Yield();

        foreach (var directory in Directory.EnumerateDirectories(localPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            await foreach (var request in PlanLocalAsync(directory,
                               CombineRemote(remotePath, Path.GetFileName(directory)), batchId, cancellationToken))
                yield return request;
        }
        foreach (var file in Directory.EnumerateFiles(localPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FtpTransferRequest(FtpTransferDirection.Upload, file,
                CombineRemote(remotePath, Path.GetFileName(file)), new FileInfo(file).Length, batchId);
        }
    }

    private static async IAsyncEnumerable<FtpTransferRequest> PlanRemoteDirectoryAsync(
        IFtpDirectorySession session,
        string remotePath,
        string localPath,
        Guid batchId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new FtpTransferRequest(FtpTransferDirection.Download, remotePath, localPath, 0,
            batchId, FtpTransferEntryKind.Directory);

        var entries = AllocateLocalNames(
            await session.ListAsync(remotePath, cancellationToken).ConfigureAwait(false));
        foreach (var (entry, localName) in entries.Where(x => x.Entry.IsDir))
        {
            await foreach (var request in PlanRemoteDirectoryAsync(session,
                               CombineRemote(remotePath, entry.Name), Path.Combine(localPath, localName),
                               batchId, cancellationToken))
                yield return request;
        }
        foreach (var (entry, localName) in entries.Where(x => !x.Entry.IsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FtpTransferRequest(FtpTransferDirection.Download,
                CombineRemote(remotePath, entry.Name), Path.Combine(localPath, localName),
                entry.Size, batchId);
        }
    }

    internal static string CombineRemote(string parent, string name)
    {
        var left = parent.Replace('\\', '/').TrimEnd('/');
        var right = name.Replace('\\', '/').Trim('/');
        if (left.Length == 0 || left == "/") return "/" + right;
        return left + "/" + right;
    }

    internal static string SafeLocalName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        var safe = name.Trim().TrimEnd('.', ' ');
        if (safe.Length == 0 || safe is "." or "..") return "_";
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (ReservedWindowsNames.Contains(stem)) safe = "_" + safe;
        return safe;
    }

    private static IReadOnlyList<(FtpRemoteEntry Entry, string LocalName)> AllocateLocalNames(
        IEnumerable<FtpRemoteEntry> entries)
    {
        var result = new List<(FtpRemoteEntry, string)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var safe = SafeLocalName(entry.Name);
            var candidate = safe;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                var extension = Path.GetExtension(safe);
                var stem = extension.Length == 0 ? safe : safe[..^extension.Length];
                candidate = $"{stem} ({suffix++}){extension}";
            }
            result.Add((entry, candidate));
        }
        return result;
    }

    private static readonly HashSet<string> ReservedWindowsNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(x => "COM" + x))
            .Concat(Enumerable.Range(1, 9).Select(x => "LPT" + x)),
        StringComparer.OrdinalIgnoreCase);
}
