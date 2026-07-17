using static AgentPack.Core.AdapterHelpers;

namespace AgentPack.Core;

/// <summary>
/// GitHub Copilot (VS Code + Copilot CLI). Project MCP servers merge into
/// .vscode/mcp.json (root key "servers"); user-scope MCP goes to the Copilot CLI
/// config ~/.copilot/mcp-config.json. Hooks are one JSON file per hook under
/// .github/hooks/ (project) or ~/.copilot/hooks/ (user).
/// See docs/provider-mapping.md for the audited matrix.
/// </summary>
public sealed class CopilotAdapter : IProviderAdapter
{
    public ProviderName Name => ProviderName.Copilot;

    public bool Detect(string root) =>
        Exists(root, ".github", "copilot-instructions.md") ||
        Exists(root, ".github", "instructions") ||
        Exists(root, ".github", "prompts") ||
        Exists(root, ".github", "hooks") ||
        Exists(root, ".vscode", "mcp.json");

    public ProviderPlan Plan(Asset asset, bool userScope)
    {
        return asset.Kind switch
        {
            AssetKind.Skills => userScope
                ? Supported(Name, asset, Path.Combine(".copilot", "skills", asset.Id), InstallMode.CopyTree)
                : Supported(Name, asset, Path.Combine(".github", "skills", asset.Id), InstallMode.CopyTree),

            AssetKind.Mcp => userScope
                ? Supported(Name, asset, Path.Combine(".copilot", "mcp-config.json"), InstallMode.MergeMcp)
                : Supported(Name, asset, Path.Combine(".vscode", "mcp.json"), InstallMode.MergeMcp),

            AssetKind.Instructions => userScope
                ? Unsupported("Copilot user-scope instructions are managed in the editor, not on disk.")
                : Supported(Name, asset, Path.Combine(".github", "instructions", asset.Id + ".instructions.md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Prompts => userScope
                ? Unsupported("Copilot user-scope prompt files are managed in the editor, not on disk.")
                : Supported(Name, asset, Path.Combine(".github", "prompts", asset.Id + ".prompt.md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Hooks => userScope
                ? Supported(Name, asset, Path.Combine(".copilot", "hooks", asset.Id + ".json"), InstallMode.MergeHook)
                : Supported(Name, asset, Path.Combine(".github", "hooks", asset.Id + ".json"), InstallMode.MergeHook),

            // The .agent.md suffix is required in both scopes; plain .md files are ignored.
            AssetKind.Agents => userScope
                ? Supported(Name, asset, Path.Combine(".copilot", "agents", asset.Id + ".agent.md"), InstallMode.CopyTree, isFileTarget: true)
                : Supported(Name, asset, Path.Combine(".github", "agents", asset.Id + ".agent.md"), InstallMode.CopyTree, isFileTarget: true),

            AssetKind.Rules => Unsupported("Copilot has no rules files — use an instructions asset (.github/instructions) instead."),
            AssetKind.Tools => Unsupported("Copilot has no generic tools directory."),
            AssetKind.Templates => Unsupported("Copilot has no templates directory."),
            _ => Unsupported($"Copilot does not support {asset.Kind.Display()}.")
        };
    }
}
