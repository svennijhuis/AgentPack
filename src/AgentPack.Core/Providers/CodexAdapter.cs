using static AgentPack.Core.AdapterHelpers;

namespace AgentPack.Core;

/// <summary>
/// OpenAI Codex CLI. Skills follow the cross-tool .agents/skills convention;
/// MCP servers live in .codex/config.toml. Codex has no hook system.
/// See docs/provider-mapping.md for the audited matrix.
/// </summary>
public sealed class CodexAdapter : IProviderAdapter
{
    public ProviderName Name => ProviderName.Codex;

    public bool Detect(string root) => Exists(root, ".codex") || Exists(root, ".agents") || Exists(root, "AGENTS.md");

    public ProviderPlan Plan(Asset asset, bool userScope)
    {
        return asset.Kind switch
        {
            AssetKind.Skills => Supported(Name, asset, Path.Combine(".agents", "skills", asset.Id), InstallMode.CopyTree),

            AssetKind.Mcp => Supported(Name, asset, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp),

            AssetKind.Instructions => userScope
                ? Supported(Name, asset, Path.Combine(".codex", "AGENTS.md"), InstallMode.CopyTree, isFileTarget: true)
                : Supported(Name, asset, "AGENTS.md", InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Prompts => Supported(Name, asset, Path.Combine(".codex", "prompts", asset.Id + ".md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Hooks => Unsupported("Codex has no hook system."),
            AssetKind.Rules => Unsupported("Codex has no rules files — use an instructions asset (AGENTS.md) instead."),
            AssetKind.Tools => Unsupported("Codex has no generic tools directory."),
            AssetKind.Templates => Unsupported("Codex has no templates directory."),
            _ => Unsupported($"Codex does not support {asset.Kind.Display()}.")
        };
    }
}
