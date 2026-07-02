using AgentPack.Core;
using AgentPack.Core.Primitives;

namespace AgentPack.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("0.1.2", 0, 1, 2, null)]
    [InlineData("2.3.4-beta.1", 2, 3, 4, "-beta.1")]
    [InlineData("1.0.0+build5", 1, 0, 0, "+build5")]
    public void ParsesValidVersions(string text, int major, int minor, int patch, string? suffix)
    {
        var version = SemVersion.Parse(text);
        Assert.Equal((major, minor, patch, suffix), (version.Major, version.Minor, version.Patch, version.Suffix));
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.x")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    public void RejectsInvalidVersions(string text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    public void OrdersVersions(string lower, string higher)
    {
        Assert.True(SemVersion.Parse(lower) < SemVersion.Parse(higher));
        Assert.True(SemVersion.Parse(higher) > SemVersion.Parse(lower));
    }

    [Fact]
    public void IsNewerThanTreatsUnparsableLockVersionAsOutdated()
    {
        Assert.True(SemVersion.Parse("1.0.0").IsNewerThan("not-a-version"));
        Assert.False(SemVersion.Parse("1.0.0").IsNewerThan("1.0.0"));
        Assert.True(SemVersion.Parse("1.0.1").IsNewerThan("1.0.0"));
    }
}
