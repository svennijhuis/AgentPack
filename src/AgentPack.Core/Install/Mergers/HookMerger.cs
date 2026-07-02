using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPack.Core;

/// <summary>
/// Installs hook content into the provider's support folder and registers it in
/// the provider's hook config. Only Claude Code (.claude/settings.json) and
/// Cursor (.cursor/hooks.json) have hook systems; other providers report
/// hooks as unsupported at the adapter level and never reach this merger.
/// </summary>
public static class HookMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Apply(Asset asset, string sourcePath, InstallTarget target, string targetPath, string scopeRoot, InstallScope scope, Action<string> backupIfExists)
    {
        var supportPath = Path.GetFullPath(Path.Combine(scopeRoot, SupportRelativePath(target.Provider, asset.Id)));
        CopyHookContent(sourcePath, supportPath, backupIfExists);

        var commandPath = ResolveHookCommand(asset, supportPath);
        var command = scope == InstallScope.User
            ? commandPath
            : "./" + Path.GetRelativePath(scopeRoot, commandPath).Replace(Path.DirectorySeparatorChar, '/');

        MergeConfig(targetPath, target.Provider, asset, command, backupIfExists);
        return ContentHash.Compute(targetPath);
    }

    /// <summary>The exact config fragment that will be merged — used for previews before applying.</summary>
    public static string Preview(Asset asset, InstallTarget target, string scopeRoot)
    {
        var command = "./" + SupportRelativePath(target.Provider, asset.Id).Replace(Path.DirectorySeparatorChar, '/') + "/" + (asset.Hook?.Command ?? "hook.sh");
        JsonObject fragment = target.Provider == ProviderName.Cursor
            ? new JsonObject
            {
                ["hooks"] = new JsonObject { [CursorEvent(asset)] = new JsonArray { CursorEntry(command) } }
            }
            : new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    [ClaudeEvent(asset)] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["matcher"] = Matcher(asset),
                            ["hooks"] = new JsonArray { ClaudeHandler(asset, command) }
                        }
                    }
                }
            };

        return fragment.ToJsonString(JsonOptions);
    }

    public static string SupportRelativePath(ProviderName provider, string assetId) => provider switch
    {
        ProviderName.Claude => Path.Combine(".claude", "hooks", assetId),
        ProviderName.Cursor => Path.Combine(".cursor", "hooks", assetId),
        _ => throw new AgentPackException($"{provider.Display()} has no hook system.")
    };

    private static void MergeConfig(string path, ProviderName provider, Asset asset, string command, Action<string> backupIfExists)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var changed = provider == ProviderName.Cursor
            ? MergeCursorHook(root, hooks, asset, command)
            : MergeClaudeHook(hooks, asset, command);

        if (!changed && File.Exists(path)) return;
        if (File.Exists(path)) backupIfExists(path);
        AtomicWrite.Text(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static bool MergeClaudeHook(JsonObject hooks, Asset asset, string command)
    {
        var eventName = ClaudeEvent(asset);
        var matcher = Matcher(asset);
        var handler = ClaudeHandler(asset, command);

        if (hooks[eventName] is not JsonArray eventArray)
        {
            hooks[eventName] = new JsonArray
            {
                new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray { handler } }
            };
            return true;
        }

        foreach (var node in eventArray.OfType<JsonObject>())
        {
            if (!matcher.Equals(node["matcher"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) continue;
            if (node["hooks"] is not JsonArray handlers)
            {
                node["hooks"] = new JsonArray { handler };
                return true;
            }

            if (handlers.Any(existing => JsonNode.DeepEquals(existing, handler))) return false;
            handlers.Add(handler);
            return true;
        }

        eventArray.Add(new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray { handler } });
        return true;
    }

    private static bool MergeCursorHook(JsonObject root, JsonObject hooks, Asset asset, string command)
    {
        root["version"] ??= 1;
        var eventName = CursorEvent(asset);
        var entry = CursorEntry(command);
        if (hooks[eventName] is not JsonArray eventArray)
        {
            hooks[eventName] = new JsonArray { entry };
            return true;
        }

        if (eventArray.Any(existing => JsonNode.DeepEquals(existing, entry))) return false;
        eventArray.Add(entry);
        return true;
    }

    private static JsonObject ClaudeHandler(Asset asset, string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = asset.Hook?.TimeoutSec ?? 30
    };

    private static JsonObject CursorEntry(string command) => new() { ["command"] = command };

    private static string ClaudeEvent(Asset asset) => (asset.Hook?.Trigger ?? HookTrigger.PreToolUse).ToString();

    private static string CursorEvent(Asset asset) => (asset.Hook?.Trigger ?? HookTrigger.PreToolUse) switch
    {
        HookTrigger.PreToolUse => "beforeShellExecution",
        HookTrigger.PostToolUse => "afterFileEdit",
        HookTrigger.Stop => "stop",
        HookTrigger.UserPromptSubmit => "beforeSubmitPrompt",
        var trigger => throw new AgentPackException(
            $"Cursor hooks do not support the {EnumParsers.CamelCase(trigger.ToString())} trigger.",
            "Supported for Cursor: preToolUse, postToolUse, stop, userPromptSubmit.")
    };

    private static string Matcher(Asset asset) => string.IsNullOrWhiteSpace(asset.Hook?.Tool) ? "Bash" : asset.Hook.Tool;

    private static void CopyHookContent(string sourcePath, string supportPath, Action<string> backupIfExists)
    {
        if (File.Exists(supportPath) || Directory.Exists(supportPath)) backupIfExists(supportPath);
        DeleteExisting(supportPath);
        if (File.Exists(sourcePath))
        {
            Directory.CreateDirectory(supportPath);
            ContentHash.CopyTree(sourcePath, Path.Combine(supportPath, Path.GetFileName(sourcePath)));
        }
        else
        {
            ContentHash.CopyTree(sourcePath, supportPath);
        }

        foreach (var script in Directory.EnumerateFiles(supportPath, "*.sh", SearchOption.AllDirectories))
        {
            ContentHash.MakeExecutable(script);
        }
    }

    private static string ResolveHookCommand(Asset asset, string supportPath)
    {
        if (!string.IsNullOrWhiteSpace(asset.Hook?.Command))
        {
            return Path.GetFullPath(Path.Combine(supportPath, asset.Hook.Command));
        }

        var hookSh = Path.Combine(supportPath, "hook.sh");
        if (File.Exists(hookSh)) return hookSh;

        return Directory.EnumerateFiles(supportPath, "*", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new AgentPackException($"Hook asset '{asset.Id}' has no executable content.");
    }

    private static void DeleteExisting(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
