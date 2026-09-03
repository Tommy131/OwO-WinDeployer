using OwOWinDeployer.App.Services.Ftp;
using Xunit;

namespace OwOWinDeployer.App.Tests.Ftp;

public sealed class FtpTransferPlannerTests
{
    [Fact]
    public async Task UploadPlanPreservesNestedFilesAndEmptyDirectories()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "project"));
        var nested = root.CreateSubdirectory("src");
        root.CreateSubdirectory("empty");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "readme.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(nested.FullName, "app.bin"), new byte[] { 1, 2, 3 });
        var batchId = Guid.NewGuid();
        var planner = new FtpTransferPlanner();

        var requests = await CollectAsync(planner.PlanUploadsAsync(
            new[] { root.FullName }, "/incoming", batchId, CancellationToken.None));

        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.Directory && x.DestinationPath == "/incoming/project");
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.Directory && x.DestinationPath == "/incoming/project/empty");
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.File && x.DestinationPath == "/incoming/project/readme.txt" && x.Size == 5);
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.File && x.DestinationPath == "/incoming/project/src/app.bin" && x.Size == 3);
        Assert.All(requests, x => Assert.Equal(batchId, x.BatchId));
    }

    [Fact]
    public async Task DownloadPlanUsesAbsoluteListingsWithoutChangingBrowserSession()
    {
        using var temp = new TempDirectory();
        var listings = new Dictionary<string, IReadOnlyList<FtpRemoteEntry>>(StringComparer.Ordinal)
        {
            ["/projects/demo"] =
            [
                new FtpRemoteEntry("src", true, 0, null),
                new FtpRemoteEntry("readme.txt", false, 5, null),
            ],
            ["/projects/demo/src"] =
            [
                new FtpRemoteEntry("app.bin", false, 3, null),
            ],
        };
        var factory = new DirectorySessionFactory(listings);
        var planner = new FtpTransferPlanner(factory);
        var connection = new FtpConnectionSnapshot("host", 21, "none", "user", "pass", "saved");
        var batchId = Guid.NewGuid();

        var requests = await CollectAsync(planner.PlanDownloadsAsync(connection,
            new[] { new FtpRemoteEntry("demo", true, 0, null) }, "/projects", temp.Path,
            batchId, CancellationToken.None));

        Assert.Equal(new[] { "/projects/demo", "/projects/demo/src" }, factory.RequestedPaths);
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.Directory && x.DestinationPath == Path.Combine(temp.Path, "demo"));
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.File && x.SourcePath == "/projects/demo/readme.txt" && x.Size == 5);
        Assert.Contains(requests, x => x.Kind == FtpTransferEntryKind.File && x.SourcePath == "/projects/demo/src/app.bin" && x.DestinationPath == Path.Combine(temp.Path, "demo", "src", "app.bin"));
    }

    [Fact]
    public async Task DownloadPlanMakesWindowsDestinationNamesSafeAndUnique()
    {
        using var temp = new TempDirectory();
        var factory = new DirectorySessionFactory(
            new Dictionary<string, IReadOnlyList<FtpRemoteEntry>>(StringComparer.Ordinal));
        var planner = new FtpTransferPlanner(factory);
        var entries = new[]
        {
            new FtpRemoteEntry("Readme", false, 1, null),
            new FtpRemoteEntry("README", false, 1, null),
            new FtpRemoteEntry("a:b.txt", false, 1, null),
            new FtpRemoteEntry("a?b.txt", false, 1, null),
            new FtpRemoteEntry("CON.txt", false, 1, null),
        };

        var requests = await CollectAsync(planner.PlanDownloadsAsync(
            new FtpConnectionSnapshot("host", 21, "none", "user", "pass", "saved"),
            entries, "/", temp.Path, Guid.NewGuid(), CancellationToken.None));
        var destinations = requests.Select(x => x.DestinationPath).ToList();

        Assert.Equal(destinations.Count, destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(destinations, path =>
            Path.GetFileNameWithoutExtension(path).Equals("CON", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<FtpTransferRequest>> CollectAsync(IAsyncEnumerable<FtpTransferRequest> source)
    {
        var values = new List<FtpTransferRequest>();
        await foreach (var value in source) values.Add(value);
        return values;
    }

    private sealed class DirectorySessionFactory : IFtpDirectorySessionFactory
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<FtpRemoteEntry>> _listings;
        public DirectorySessionFactory(IReadOnlyDictionary<string, IReadOnlyList<FtpRemoteEntry>> listings) => _listings = listings;
        public List<string> RequestedPaths { get; } = new();

        public Task<IFtpDirectorySession> ConnectAsync(FtpConnectionSnapshot connection, CancellationToken cancellationToken)
            => Task.FromResult<IFtpDirectorySession>(new Session(this));

        private sealed class Session : IFtpDirectorySession
        {
            private readonly DirectorySessionFactory _owner;
            public Session(DirectorySessionFactory owner) => _owner = owner;

            public Task<IReadOnlyList<FtpRemoteEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
            {
                _owner.RequestedPaths.Add(remotePath);
                return Task.FromResult(_owner._listings[remotePath]);
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
