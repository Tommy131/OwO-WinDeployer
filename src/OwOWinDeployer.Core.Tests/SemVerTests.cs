using OwOWinDeployer.Core.Util;
using Xunit;

namespace OwOWinDeployer.Core.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.3.1", "1.3.0", 1)]      // patch newer
    [InlineData("1.3.0", "1.3.1", -1)]
    [InlineData("1.10.0", "1.9.0", 1)]     // numeric, not lexical
    [InlineData("1.3.1", "1.3.1", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.2.3.1", "1.2.3", 1)]    // 4-part patch-of-patch core
    [InlineData("v1.3.1", "1.3.1", 0)]     // leading v ignored
    [InlineData("1.3.1+build9", "1.3.1", 0)] // build metadata ignored
    public void Compares_release_cores(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    [Theory]
    [InlineData("1.3.1-rc.1", "1.3.1", -1)]        // a pre-release precedes its release
    [InlineData("1.3.1", "1.3.1-rc.1", 1)]
    [InlineData("1.3.1-rc.1", "1.3.1-rc.2", -1)]   // numeric pre-release identifiers
    [InlineData("1.3.1-rc.2", "1.3.1-rc.1", 1)]
    [InlineData("1.3.1-rc.1", "1.3.1-rc.1", 0)]
    [InlineData("1.3.1-alpha", "1.3.1-beta", -1)]  // alphanumeric ordering
    [InlineData("1.3.1-rc.1", "1.3.1-rc.1.1", -1)] // smaller identifier set is lower
    [InlineData("1.3.2-rc.1", "1.3.1", 1)]         // core dominates the pre-release rule
    public void Orders_prereleases(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    [Theory]
    [InlineData("1.3.1-rc.1", true)]
    [InlineData("1.3.1", false)]
    [InlineData("v1.3.1-beta.2+abc", true)]
    [InlineData("1.3.1+build", false)]
    public void Detects_prerelease(string v, bool expected)
        => Assert.Equal(expected, SemVer.IsPrerelease(v));
}
