using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// The full provider × kind matrix, mirroring docs/provider-mapping.md.
/// A change here must be a deliberate provider-mapping change, never an accident.
/// </summary>
public class ProviderAdapterTests
{
    public static IEnumerable<object?[]> FullProviderKindMatrix()
    {
        foreach (var provider in ProviderNames.All)
            foreach (var kind in AssetKinds.All)
                foreach (var userScope in new[] { false, true })
                {
                    var expected = Expected(provider, kind, userScope);
                    yield return [provider, kind, userScope, expected?.Path, expected?.Mode ?? default, expected?.FileTarget ?? false];
                }
    }

    [Fact]
    public void MatrixDeclaresEveryProviderKindAndScope() =>
        Assert.Equal(ProviderNames.All.Count * AssetKinds.All.Count * 2, FullProviderKindMatrix().Count());

    [Theory]
    [MemberData(nameof(FullProviderKindMatrix))]
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

    private sealed record ExpectedTarget(string Path, InstallMode Mode, bool FileTarget = false);

    private static ExpectedTarget? Expected(ProviderName provider, AssetKind kind, bool user) => (provider, kind, user) switch
    {
        (ProviderName.Claude, AssetKind.Agents, _) => T(".claude/agents/demo.md", InstallMode.RenderAgent, true),
        (ProviderName.Claude, AssetKind.Skills, _) => T(".claude/skills/demo", InstallMode.CopyTree),
        (ProviderName.Claude, AssetKind.Hooks, _) => T(".claude/settings.json", InstallMode.MergeHook),
        (ProviderName.Claude, AssetKind.Mcp, false) => T(".mcp.json", InstallMode.MergeMcp),
        (ProviderName.Claude, AssetKind.Mcp, true) => T(".claude.json", InstallMode.MergeMcp),
        (ProviderName.Claude, AssetKind.Instructions, false) => T("CLAUDE.md", InstallMode.CopyTree, true),
        (ProviderName.Claude, AssetKind.Instructions, true) => T(".claude/CLAUDE.md", InstallMode.CopyTree, true),
        (ProviderName.Claude, AssetKind.Rules, _) => T(".claude/rules/demo.md", InstallMode.CopyTree, true),
        (ProviderName.Claude, AssetKind.Prompts, _) => T(".claude/commands/demo.md", InstallMode.CopyTree, true),
        (ProviderName.Claude, AssetKind.Tools or AssetKind.Templates, _) => null,

        (ProviderName.Codex, AssetKind.Agents, _) => T(".codex/agents/demo.toml", InstallMode.RenderAgent, true),
        (ProviderName.Codex, AssetKind.Skills, _) => T(".agents/skills/demo", InstallMode.CopyTree),
        (ProviderName.Codex, AssetKind.Hooks, _) => T(".codex/hooks.json", InstallMode.MergeHook),
        (ProviderName.Codex, AssetKind.Mcp, _) => T(".codex/config.toml", InstallMode.MergeMcp),
        (ProviderName.Codex, AssetKind.Instructions, false) => T("AGENTS.md", InstallMode.CopyTree, true),
        (ProviderName.Codex, AssetKind.Instructions, true) => T(".codex/AGENTS.md", InstallMode.CopyTree, true),
        (ProviderName.Codex, AssetKind.Prompts, false) => null,
        (ProviderName.Codex, AssetKind.Prompts, true) => T(".codex/prompts/demo.md", InstallMode.CopyTree, true),
        (ProviderName.Codex, AssetKind.Rules or AssetKind.Tools or AssetKind.Templates, _) => null,

        (ProviderName.Copilot, AssetKind.Agents, false) => T(".github/agents/demo.agent.md", InstallMode.RenderAgent, true),
        (ProviderName.Copilot, AssetKind.Agents, true) => T(".copilot/agents/demo.agent.md", InstallMode.RenderAgent, true),
        (ProviderName.Copilot, AssetKind.Skills, false) => T(".github/skills/demo", InstallMode.CopyTree),
        (ProviderName.Copilot, AssetKind.Skills, true) => T(".copilot/skills/demo", InstallMode.CopyTree),
        (ProviderName.Copilot, AssetKind.Hooks, false) => T(".github/hooks/demo.json", InstallMode.MergeHook),
        (ProviderName.Copilot, AssetKind.Hooks, true) => T(".copilot/hooks/demo.json", InstallMode.MergeHook),
        (ProviderName.Copilot, AssetKind.Mcp, false) => T(".github/mcp.json", InstallMode.MergeMcp),
        (ProviderName.Copilot, AssetKind.Mcp, true) => T(".copilot/mcp-config.json", InstallMode.MergeMcp),
        (ProviderName.Copilot, AssetKind.Instructions, false) => T(".github/copilot-instructions.md", InstallMode.CopyTree, true),
        (ProviderName.Copilot, AssetKind.Instructions, true) => T(".copilot/copilot-instructions.md", InstallMode.CopyTree, true),
        (ProviderName.Copilot, AssetKind.Prompts, false) => T(".github/prompts/demo.prompt.md", InstallMode.CopyTree, true),
        (ProviderName.Copilot, AssetKind.Prompts, true) => null,
        (ProviderName.Copilot, AssetKind.Rules or AssetKind.Tools or AssetKind.Templates, _) => null,

        (ProviderName.Cursor, AssetKind.Agents, _) => T(".cursor/agents/demo.md", InstallMode.RenderAgent, true),
        (ProviderName.Cursor, AssetKind.Skills, _) => T(".cursor/skills/demo", InstallMode.CopyTree),
        (ProviderName.Cursor, AssetKind.Hooks, _) => T(".cursor/hooks.json", InstallMode.MergeHook),
        (ProviderName.Cursor, AssetKind.Mcp, _) => T(".cursor/mcp.json", InstallMode.MergeMcp),
        (ProviderName.Cursor, AssetKind.Instructions, false) => T("AGENTS.md", InstallMode.CopyTree, true),
        (ProviderName.Cursor, AssetKind.Instructions, true) => null,
        (ProviderName.Cursor, AssetKind.Rules, false) => T(".cursor/rules/demo.mdc", InstallMode.CopyTree, true),
        (ProviderName.Cursor, AssetKind.Rules, true) => null,
        (ProviderName.Cursor, AssetKind.Prompts, false) => T(".cursor/commands/demo.md", InstallMode.CopyTree, true),
        (ProviderName.Cursor, AssetKind.Prompts, true) => null,
        (ProviderName.Cursor, AssetKind.Tools or AssetKind.Templates, _) => null,
        _ => throw new InvalidOperationException($"Provider matrix has no declaration for {provider}/{kind}/{(user ? "user" : "project")}.")
    };

    private static ExpectedTarget T(string path, InstallMode mode, bool fileTarget = false) =>
        new(path, mode, fileTarget);

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
