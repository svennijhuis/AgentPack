using AgentPack.Core;

namespace AgentPack.Tests;

public class GroupMatchTests
{
    [Theory]
    [InlineData("csharp", "csharp", true)]            // exact
    [InlineData("csharp", "csharp/review", true)]     // parent matches subgroup
    [InlineData("csharp/review", "csharp/review", true)]
    [InlineData("CSHARP", "csharp/Review", true)]     // case-insensitive
    [InlineData("csharp/review", "csharp", false)]    // subgroup filter does not match parent
    [InlineData("csharp", "csharpx", false)]          // only matches at a '/' boundary
    [InlineData("csharp", "typescript/review", false)]
    public void Matches_HonorsHierarchy(string filter, string assetGroup, bool expected)
    {
        Assert.Equal(expected, GroupMatch.Matches(filter, assetGroup));
    }

    [Theory]
    [InlineData("csharp/review", "csharp")]
    [InlineData("csharp", "csharp")]
    [InlineData("react/format", "react")]
    public void TopLevel_ReturnsFirstSegment(string group, string expected)
    {
        Assert.Equal(expected, GroupMatch.TopLevel(group));
    }
}
