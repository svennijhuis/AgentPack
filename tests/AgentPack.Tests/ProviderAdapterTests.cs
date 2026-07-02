using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// The full provider × kind matrix, mirroring docs/provider-mapping.md.
/// A change here must be a deliberate provider-mapping change, never an accident.
/// </summary>
public class ProviderAdapterTests
{
    [Theory]
    // provider, kind, userScope, expected relative path (null = unsupported), mode, fileTarget
    [InlineData(ProviderName.Claude, AssetKind.Skills, false, ".claude/skills/demo", InstallMode.CopyTree, false)]
    [InlineData(ProviderName.Claude, AssetKind.Hooks, false, ".claude/settings.json", InstallMode.MergeHook, false)]
    [InlineData(ProviderName.Claude, AssetKind.Mcp, false, ".mcp.json", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Claude, AssetKind.Mcp, true, ".claude.json", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Claude, AssetKind.Instructions, false, "CLAUDE.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Claude, AssetKind.Instructions, true, ".claude/CLAUDE.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Claude, AssetKind.Prompts, false, ".claude/commands/demo.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Claude, AssetKind.Rules, false, null, default(InstallMode), false)]
    [InlineData(ProviderName.Codex, AssetKind.Skills, false, ".agents/skills/demo", InstallMode.CopyTree, false)]
    [InlineData(ProviderName.Codex, AssetKind.Mcp, false, ".codex/config.toml", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Codex, AssetKind.Instructions, false, "AGENTS.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Codex, AssetKind.Instructions, true, ".codex/AGENTS.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Codex, AssetKind.Prompts, false, ".codex/prompts/demo.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Codex, AssetKind.Hooks, false, null, default(InstallMode), false)]
    [InlineData(ProviderName.Codex, AssetKind.Rules, false, null, default(InstallMode), false)]
    [InlineData(ProviderName.Copilot, AssetKind.Skills, false, ".github/skills/demo", InstallMode.CopyTree, false)]
    [InlineData(ProviderName.Copilot, AssetKind.Skills, true, ".copilot/skills/demo", InstallMode.CopyTree, false)]
    [InlineData(ProviderName.Copilot, AssetKind.Mcp, false, ".vscode/mcp.json", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Copilot, AssetKind.Mcp, true, ".copilot/mcp-config.json", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Copilot, AssetKind.Instructions, false, ".github/instructions/demo.instructions.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Copilot, AssetKind.Prompts, false, ".github/prompts/demo.prompt.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Copilot, AssetKind.Hooks, false, null, default(InstallMode), false)]
    [InlineData(ProviderName.Cursor, AssetKind.Skills, false, ".cursor/skills/demo", InstallMode.CopyTree, false)]
    [InlineData(ProviderName.Cursor, AssetKind.Rules, false, ".cursor/rules/demo.mdc", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Cursor, AssetKind.Hooks, false, ".cursor/hooks.json", InstallMode.MergeHook, false)]
    [InlineData(ProviderName.Cursor, AssetKind.Mcp, false, ".cursor/mcp.json", InstallMode.MergeMcp, false)]
    [InlineData(ProviderName.Cursor, AssetKind.Instructions, false, "AGENTS.md", InstallMode.CopyTree, true)]
    [InlineData(ProviderName.Cursor, AssetKind.Prompts, false, ".cursor/commands/demo.md", InstallMode.CopyTree, true)]
    public void ProviderKindMatrix(ProviderName provider, AssetKind kind, bool userScope, string? expectedPath, InstallMode mode, bool fileTarget)
    {
        var asset = TestData.Asset(kind, "demo");
        var plan = ProviderRegistry.Get(provider).Plan(asset, userScope);

        if (expectedPath is null)
        {
            var unsupported = Assert.IsType<ProviderPlan.Unsupported>(plan);
            Assert.False(string.IsNullOrWhiteSpace(unsupported.Reason));
            return;
        }

        var supported = Assert.IsType<ProviderPlan.Supported>(plan);
        Assert.Equal(expectedPath.Replace('/', Path.DirectorySeparatorChar), supported.Target.RelativePath);
        Assert.Equal(mode, supported.Target.Mode);
        Assert.Equal(fileTarget, supported.Target.IsFileTarget);
    }

    [Fact]
    public void ToolsAndTemplatesAreUnsupportedEverywhere()
    {
        foreach (var adapter in ProviderRegistry.All)
        {
            Assert.IsType<ProviderPlan.Unsupported>(adapter.Plan(TestData.Asset(AssetKind.Tools, "t"), false));
            Assert.IsType<ProviderPlan.Unsupported>(adapter.Plan(TestData.Asset(AssetKind.Templates, "t"), false));
        }
    }

    [Fact]
    public void DetectFindsProvidersFromWorkspaceMarkers()
    {
        using var temp = new TempDir();
        Assert.Empty(ProviderRegistry.Detect(temp.Path));

        Directory.CreateDirectory(Path.Combine(temp.Path, ".claude"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".cursor"));
        File.WriteAllText(Path.Combine(temp.Path, "AGENTS.md"), "# agents\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, ".vscode"));
        File.WriteAllText(Path.Combine(temp.Path, ".vscode", "mcp.json"), "{}");

        var detected = ProviderRegistry.Detect(temp.Path);
        Assert.Equal([ProviderName.Claude, ProviderName.Codex, ProviderName.Copilot, ProviderName.Cursor], detected);
    }
}
