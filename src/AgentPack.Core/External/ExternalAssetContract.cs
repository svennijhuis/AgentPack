namespace AgentPack.Core;

/// <summary>
/// Validates the fetched shape of an external asset before it can be installed.
/// Upstream content is data; provider permissions and installation metadata remain
/// controlled by the reviewed AgentPack manifest.
/// </summary>
public static class ExternalAssetContract
{
    public static void Validate(Asset asset, string sourcePath)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            throw Invalid(asset, $"fetched content does not exist at '{sourcePath}'");

        switch (asset.Kind)
        {
            case AssetKind.Agents:
                RequireOneMarkdown(asset, sourcePath,
                    ["AGENT.md", asset.Id + ".agent.md", asset.Id + ".md"]);
                break;

            case AssetKind.Skills:
                RequireDirectory(asset, sourcePath);
                if (!File.Exists(Path.Combine(sourcePath, "SKILL.md")))
                    throw Invalid(asset, "a skill directory must contain SKILL.md at its root");
                break;

            case AssetKind.Hooks:
                RequireDirectory(asset, sourcePath);
                if (asset.Hook is null || string.IsNullOrWhiteSpace(asset.Hook.Command))
                    throw Invalid(asset, "a hook requires reviewed hook.command metadata");
                var command = SafeChildPath(asset, sourcePath, asset.Hook.Command);
                if (!File.Exists(command))
                    throw Invalid(asset, $"hook.command '{asset.Hook.Command}' was not found in the fetched directory");
                break;

            case AssetKind.Mcp:
                // This parses raw upstream mcp.json when no typed manifest metadata
                // exists and also rejects embedded environment values.
                if (asset.Mcp is null && Directory.Exists(sourcePath) &&
                    !File.Exists(Path.Combine(sourcePath, "mcp.json")))
                    throw Invalid(asset, "a raw MCP directory must contain mcp.json at its root");
                var servers = McpMerger.BuildServers(asset, sourcePath, ProviderName.Claude, InstallScope.Project);
                foreach (var (serverId, node) in servers)
                {
                    if (node is not System.Text.Json.Nodes.JsonObject server)
                        throw Invalid(asset, $"MCP server '{serverId}' is not an object");
                    var type = server["type"]?.GetValue<string>() ?? "stdio";
                    var required = type.Equals("stdio", StringComparison.OrdinalIgnoreCase) ? "command" : "url";
                    if (string.IsNullOrWhiteSpace(server[required]?.GetValue<string>()))
                        throw Invalid(asset, $"MCP server '{serverId}' requires {required}");
                }
                break;

            case AssetKind.Instructions:
            case AssetKind.Prompts:
                RequireOneFileWithExtension(asset, sourcePath, ".md");
                break;

            case AssetKind.Rules:
                RequireOneFileWithExtension(asset, sourcePath, ".mdc");
                break;

            case AssetKind.Tools:
            case AssetKind.Templates:
                throw Invalid(asset, "this kind has no provider-native external installation contract");
        }
    }

    private static void RequireOneMarkdown(Asset asset, string sourcePath, IReadOnlyList<string> preferredNames)
    {
        if (File.Exists(sourcePath)) return;
        var markdown = Directory.EnumerateFiles(sourcePath, "*.md", SearchOption.AllDirectories).ToList();
        if (preferredNames.Any(name => markdown.Any(path =>
                Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)))) return;
        if (markdown.Count == 1) return;
        throw Invalid(asset,
            $"expected one Markdown agent file or one named {string.Join(", ", preferredNames)}, found {markdown.Count}");
    }

    private static void RequireOneFileWithExtension(Asset asset, string sourcePath, string extension)
    {
        if (File.Exists(sourcePath))
        {
            if (!Path.GetExtension(sourcePath).Equals(extension, StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(sourcePath).Equals("content", StringComparison.OrdinalIgnoreCase))
                throw Invalid(asset, $"expected a {extension} file");
            return;
        }

        var files = Directory.EnumerateFiles(sourcePath, "*" + extension, SearchOption.AllDirectories).ToList();
        if (files.Count != 1)
            throw Invalid(asset, $"expected exactly one {extension} file, found {files.Count}");
    }

    private static void RequireDirectory(Asset asset, string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
            throw Invalid(asset, "the source URL must select a directory, not an individual file");
    }

    private static string SafeChildPath(Asset asset, string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw Invalid(asset, "hook.command must be relative to the fetched directory");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.Ordinal))
            throw Invalid(asset, "hook.command cannot escape the fetched directory");
        return full;
    }

    private static AgentPackException Invalid(Asset asset, string reason) => new(
        $"External {asset.Kind.Display()} asset '{asset.Id}' has invalid fetched content: {reason}.",
        "Point the pinned source at a native asset root or correct its reviewed AgentPack manifest.",
        ExitCodes.ValidationFailed);
}
