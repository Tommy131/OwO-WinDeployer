using System.Net;
using System.Net.Sockets;
using OwOWinDeployer.App.Services.Ftp;
using Xunit;

namespace OwOWinDeployer.App.Tests.Ftp;

public sealed class FtpClientRangeIntegrationTests
{
    [Fact]
    public async Task RangeDownloadWritesOnlyRequestedRemoteBytesAndServerRemainsResponsive()
    {
        using var home = new TempDirectory();
        var content = Enumerable.Range(0, 4096).Select(x => (byte)(x % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(home.Path, "source.bin"), content);
        var destination = Path.Combine(home.Path, "range.bin");
        await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                         FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            target.SetLength(content.Length);
        }

        var (hash, salt) = FtpPassword.Create("secret");
        var controlPort = FindFreePort();
        var passiveStart = FindFreeRange(8);
        using var server = new FtpServer();
        server.Start(new FtpServerConfig
        {
            ListenAddress = IPAddress.Loopback.ToString(),
            Port = controlPort,
            TlsMode = "none",
            PassiveMin = passiveStart,
            PassiveMax = passiveStart + 7,
            Users =
            {
                new FtpUser
                {
                    Name = "tester",
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    Home = home.Path,
                    Permissions = FtpPerm.Full,
                    UseGroupPermissions = false,
                },
            },
        });

        var snapshot = new FtpConnectionSnapshot("127.0.0.1", controlPort, "none", "tester", "secret", "test");
        var factory = new FtpTransferSessionFactory();
        using (var session = await factory.ConnectAsync(snapshot, CancellationToken.None))
        {
            await session.DownloadRangeAsync("/source.bin", destination, 777, 1333, null, CancellationToken.None);
        }

        var actual = await File.ReadAllBytesAsync(destination);
        Assert.Equal(content.AsSpan(777, 1333).ToArray(), actual.AsSpan(777, 1333).ToArray());
        Assert.All(actual.AsSpan(0, 777).ToArray(), value => Assert.Equal(0, value));
        Assert.All(actual.AsSpan(2110).ToArray(), value => Assert.Equal(0, value));

        using var browser = await factory.ConnectAsync(snapshot, CancellationToken.None);
        var client = Assert.IsType<FtpClient>(browser);
        var listing = await client.ListAsync(CancellationToken.None);
        Assert.Contains(listing, x => x.Name == "source.bin" && x.Size == content.Length);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FindFreeRange(int count)
    {
        for (var start = 40000; start <= 60000 - count; start += count)
        {
            var listeners = new List<TcpListener>();
            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    var listener = new TcpListener(IPAddress.Loopback, start + offset);
                    listener.Start();
                    listeners.Add(listener);
                }
                return start;
            }
            catch (SocketException) { }
            finally
            {
                foreach (var listener in listeners) listener.Stop();
            }
        }
        throw new InvalidOperationException("No free passive FTP port range found.");
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
