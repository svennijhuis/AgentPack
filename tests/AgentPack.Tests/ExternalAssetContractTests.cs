using AgentPack.Core;

namespace AgentPack.Tests;

public class ExternalAssetContractTests
{
    public static TheoryData<AssetKind, string, string> SupportedKinds => new()
    {
        { AssetKind.Agents, "AGENT.md", "# Agent\n" },
        { AssetKind.Skills, "SKILL.md", "---\nname: demo\ndescription: Demo.\n---\n" },
        { AssetKind.Hooks, "hook.sh", "#!/usr/bin/env bash\n" },
        { AssetKind.Mcp, "mcp.json", "{\"name\":\"demo\",\"transport\":\"stdio\",\"command\":\"demo-server\"}" },
        { AssetKind.Instructions, "instructions.md", "# Instructions\n" },
        { AssetKind.Prompts, "prompt.md", "# Prompt\n" },
        { AssetKind.Rules, "rule.mdc", "# Rule\n" }
    };

    [Theory]
    [MemberData(nameof(SupportedKinds))]
    public void EverySupportedExternalKindHasAValidatedContentContract(
        AssetKind kind, string fileName, string content)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, fileName), content);
        var hook = kind == AssetKind.Hooks ? new HookSpec { Command = "hook.sh", Tool = "Bash" } : null;
        var asset = TestData.Asset(kind, "demo", hook: hook);

        ExternalAssetContract.Validate(asset, temp.Path);
    }

    [Fact]
    public void ExternalSkillMustSelectTheSkillDirectory()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "SKILL.md");
        File.WriteAllText(file, "# Skill\n");

        var ex = Assert.Throws<AgentPackException>(() =>
            ExternalAssetContract.Validate(TestData.Asset(AssetKind.Skills, "demo"), file));

        Assert.Contains("directory", ex.Message);
    }

    [Fact]
    public void ExternalHookCommandMustExistInsideFetchedDirectory()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Hooks, "guard", hook: new HookSpec { Command = "missing.sh" });

        var ex = Assert.Throws<AgentPackException>(() => ExternalAssetContract.Validate(asset, temp.Path));

        Assert.Contains("missing.sh", ex.Message);
    }

    [Fact]
    public void RawExternalMcpCannotContainEnvironmentValues()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "mcp.json"),
            "{\"name\":\"demo\",\"transport\":\"stdio\",\"command\":\"run\",\"env\":{\"TOKEN\":\"secret\"}}");

        var ex = Assert.Throws<AgentPackException>(() =>
            ExternalAssetContract.Validate(TestData.Asset(AssetKind.Mcp, "demo"), temp.Path));

        Assert.Contains("by name", ex.Message);
    }

    [Theory]
    [InlineData(AssetKind.Tools)]
    [InlineData(AssetKind.Templates)]
    public void KindsWithoutNativeContractsAreRejected(AssetKind kind)
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "content.md"), "content\n");

        Assert.Throws<AgentPackException>(() =>
            ExternalAssetContract.Validate(TestData.Asset(kind, "demo"), temp.Path));
    }

    [Fact]
    public void ExternalResolverEnforcesContractImmediatelyAfterGitFetch()
    {
        using var temp = new TempDir();
        var repo = Path.Combine(temp.Path, "upstream.git");
        Directory.CreateDirectory(repo);
        Git(repo, ["init"]);
        Git(repo, ["config", "user.email", "agentpack@example.test"]);
        Git(repo, ["config", "user.name", "AgentPack"]);
        File.WriteAllText(Path.Combine(repo, "README.md"), "not a skill\n");
        Git(repo, ["add", "README.md"]);
        Git(repo, ["commit", "-m", "initial"]);
        var sha = Git(repo, ["rev-parse", "HEAD"]).Trim();
        var paths = TestData.Paths(temp, "consumer");
        var asset = TestData.Asset(AssetKind.Skills, "bad-skill",
            source: new AssetSource.External(repo, sha, null, null, "MIT"));

        var ex = Assert.Throws<AgentPackException>(() => new ExternalResolver(paths).ResolveToCache(asset));

        Assert.Contains("SKILL.md", ex.Message);
    }

    private static string Git(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var result = ProcessRunner.Run("git", arguments, workingDirectory);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output;
    }
}
