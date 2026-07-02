using static AgentPack.Core.AdapterHelpers;

namespace AgentPack.Core;

/// <summary>
/// Claude Code. Project scope paths are relative to the repo root;
/// user scope paths are relative to the user's home directory.
/// See docs/provider-mapping.md for the audited matrix.
/// </summary>
public sealed class ClaudeAdapter : IProviderAdapter
{
    public ProviderName Name => ProviderName.Claude;

    public bool Detect(string root) => Exists(root, ".claude") || Exists(root, "CLAUDE.md");

    public ProviderPlan Plan(Asset asset, bool userScope)
    {
        return asset.Kind switch
        {
            AssetKind.Skills => Supported(Name, asset, Path.Combine(".claude", "skills", asset.Id), InstallMode.CopyTree),

            AssetKind.Hooks => Supported(Name, asset, Path.Combine(".claude", "settings.json"), InstallMode.MergeHook),

            AssetKind.Mcp => userScope
                ? Supported(Name, asset, ".claude.json", InstallMode.MergeMcp)
                : Supported(Name, asset, ".mcp.json", InstallMode.MergeMcp),

            AssetKind.Instructions => userScope
                ? Supported(Name, asset, Path.Combine(".claude", "CLAUDE.md"), InstallMode.CopyTree, isFileTarget: true)
                : Supported(Name, asset, "CLAUDE.md", InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Prompts => Supported(Name, asset, Path.Combine(".claude", "commands", asset.Id + ".md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Rules => Unsupported("Claude Code has no rules files — use an instructions asset (CLAUDE.md) instead."),
            AssetKind.Tools => Unsupported("Claude Code has no generic tools directory."),
            AssetKind.Templates => Unsupported("Claude Code has no templates directory."),
            _ => Unsupported($"Claude Code does not support {asset.Kind.Display()}.")
        };
    }
}
