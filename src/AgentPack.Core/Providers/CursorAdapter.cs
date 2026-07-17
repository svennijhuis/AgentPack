using static AgentPack.Core.AdapterHelpers;

namespace AgentPack.Core;

/// <summary>
/// Cursor. Rules are .mdc files under .cursor/rules; hooks merge into
/// .cursor/hooks.json; MCP servers merge into .cursor/mcp.json.
/// See docs/provider-mapping.md for the audited matrix.
/// </summary>
public sealed class CursorAdapter : IProviderAdapter
{
    public ProviderName Name => ProviderName.Cursor;

    public bool Detect(string root) => Exists(root, ".cursor");

    public ProviderPlan Plan(Asset asset, bool userScope)
    {
        return asset.Kind switch
        {
            AssetKind.Skills => Supported(Name, asset, Path.Combine(".cursor", "skills", asset.Id), InstallMode.CopyTree),

            AssetKind.Rules => Supported(Name, asset, Path.Combine(".cursor", "rules", asset.Id + ".mdc"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Hooks => Supported(Name, asset, Path.Combine(".cursor", "hooks.json"), InstallMode.MergeHook),

            AssetKind.Mcp => Supported(Name, asset, Path.Combine(".cursor", "mcp.json"), InstallMode.MergeMcp),

            AssetKind.Instructions => userScope
                ? Unsupported("Cursor user-scope instructions are managed in the app (User Rules), not on disk.")
                : Supported(Name, asset, "AGENTS.md", InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Prompts => Supported(Name, asset, Path.Combine(".cursor", "commands", asset.Id + ".md"), InstallMode.CopyTree, isFileTarget: true),

            // Cursor discovers agents only at the folder root — never nest them.
            AssetKind.Agents => Supported(Name, asset, Path.Combine(".cursor", "agents", asset.Id + ".md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Tools => Unsupported("Cursor has no generic tools directory."),
            AssetKind.Templates => Unsupported("Cursor has no templates directory."),
            _ => Unsupported($"Cursor does not support {asset.Kind.Display()}.")
        };
    }
}
