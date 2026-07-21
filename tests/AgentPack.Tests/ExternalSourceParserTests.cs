using AgentPack.Core;

namespace AgentPack.Tests;

public class ExternalSourceParserTests
{
    private const string Sha = "9d2f1ae187231d8199c64b5b762e1bdf2244733d";

    [Fact]
    public void GitHubTreeUrlResolvesRepoAndPath()
    {
        var source = new AssetSource.External($"https://github.com/anthropics/skills/tree/main/skills/pdf", Sha, null, null, null);
        var resolved = ExternalSourceParser.Resolve(source);

        Assert.Equal("https://github.com/anthropics/skills.git", resolved.Repo);
        Assert.Equal(Sha, resolved.Ref);
        Assert.Equal("skills/pdf", resolved.Path);
    }

    [Fact]
    public void DeeplyNestedTreeUrlKeepsTheFullSubpath()
    {
        // Catalog-style repos nest skills (e.g. mattpocock/skills:
        // skills/engineering/code-review); the whole subpath must survive.
        var source = new AssetSource.External(
            "https://github.com/mattpocock/skills/tree/main/skills/engineering/code-review", Sha, null, null, null);
        var resolved = ExternalSourceParser.Resolve(source);

        Assert.Equal("https://github.com/mattpocock/skills.git", resolved.Repo);
        Assert.Equal(Sha, resolved.Ref);
        Assert.Equal("skills/engineering/code-review", resolved.Path);
    }

    [Fact]
    public void PlainGitHubRepoUrlResolves()
    {
        var source = new AssetSource.External("https://github.com/example/ai-skills", "v1.2.0", "skills/x", null, null);
        var resolved = ExternalSourceParser.Resolve(source);

        Assert.Equal("https://github.com/example/ai-skills.git", resolved.Repo);
        Assert.Equal("skills/x", resolved.Path);
    }

    [Fact]
    public void AzureDevOpsUrlResolvesRepoAndPath()
    {
        var source = new AssetSource.External(
            "https://dev.azure.com/org/project/_git/repo?path=/skills/review", Sha, null, null, null);
        var resolved = ExternalSourceParser.Resolve(source);

        Assert.Equal("https://dev.azure.com/org/project/_git/repo", resolved.Repo);
        Assert.Equal("skills/review", resolved.Path);
    }

    [Fact]
    public void UnsupportedUrlThrowsWithHint()
    {
        var source = new AssetSource.External("https://example.com/something", Sha, null, null, null);
        var ex = Assert.Throws<AgentPackException>(() => ExternalSourceParser.Resolve(source));
        Assert.Contains("Unsupported external source URL", ex.Message);
        Assert.NotNull(ex.Hint);
    }

    [Fact]
    public void ShorthandSplitsUrlAndRef()
    {
        var (url, reference) = ExternalSourceParser.SplitShorthand(
            $"https://github.com/anthropics/skills/tree/main/skills/pdf@{Sha}");

        Assert.Equal("https://github.com/anthropics/skills/tree/main/skills/pdf", url);
        Assert.Equal(Sha, reference);
    }

    [Fact]
    public void ShorthandWithoutRefUsesPinnedTreeRef()
    {
        var (_, movingRef) = ExternalSourceParser.SplitShorthand("https://github.com/o/r/tree/main/path");
        Assert.Null(movingRef); // 'main' is a moving ref, not accepted as a pin

        var (_, pinned) = ExternalSourceParser.SplitShorthand($"https://github.com/o/r/tree/{Sha}/path");
        Assert.Equal(Sha, pinned);
    }

    [Fact]
    public void RepositoryLabelUsesGitHubOwnerAndRepoAsAttribution()
    {
        var source = new AssetSource.External(
            "https://github.com/example-org/agent-assets/tree/main/skills/review", Sha, null, null, "MIT");

        Assert.Equal("example-org/agent-assets", ExternalSourceParser.RepositoryLabel(source));
    }
}
