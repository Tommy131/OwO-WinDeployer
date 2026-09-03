using OwOWinDeployer.App.Services.Ftp;
using Xunit;

namespace OwOWinDeployer.App.Tests.Ftp;

public sealed class FtpLogBufferTests
{
    [Fact]
    public void SnapshotCoalescesUpdatesAndKeepsOnlyNewestLines()
    {
        var buffer = new FtpLogBuffer(capacity: 3);
        buffer.Append("one");
        buffer.Append("two");
        buffer.Append("three");
        buffer.Append("four");

        Assert.True(buffer.TryTakeSnapshot(out var snapshot));
        Assert.Equal("two\nthree\nfour", snapshot);
        Assert.False(buffer.TryTakeSnapshot(out _));

        buffer.Append("five");
        Assert.True(buffer.TryTakeSnapshot(out snapshot));
        Assert.Equal("three\nfour\nfive", snapshot);
    }
}
