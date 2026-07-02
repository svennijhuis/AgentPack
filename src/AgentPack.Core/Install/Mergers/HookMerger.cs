using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPack.Core;

/// <summary>
/// Installs hook content into the provider's support folder and registers it in
/// the provider's hook config. All four providers support hooks, in three dialects:
///
/// - Claude Code (.claude/settings.json) and Codex (.codex/hooks.json): shared file,
///   PascalCase events, entries wrapped in a matcher group.
/// - Cursor (.cursor/hooks.json): shared file, version 1, camelCase events, flat entries.
/// - Copilot CLI (.github/hooks/&lt;id&gt;.json, user: ~/.copilot/hooks/&lt;id&gt;.json):
///   one file per hook, version 1, camelCase events, bash/powershell entries.
/// </summary>
public static class HookMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Apply(Asset asset, string sourcePath, InstallTarget target, string targetPath, string scopeRoot, InstallScope scope, Action<string> backupIfExists)
    {
        var supportPath = Path.GetFullPath(Path.Combine(scopeRoot, SupportRelativePath(target.Provider, asset.Id, scope)));
        CopyHookContent(sourcePath, supportPath, backupIfExists);

        var commandPath = ResolveHookCommand(asset, supportPath);
        var command = scope == InstallScope.User
            ? commandPath
            : "./" + Path.GetRelativePath(scopeRoot, commandPath).Replace(Path.DirectorySeparatorChar, '/');

        switch (target.Provider)
        {
            case ProviderName.Claude:
            case ProviderName.Codex:
                MergeSharedConfig(targetPath, asset, backupIfExists,
                    (hooks, changedAsset) => MergeClaudeStyleHook(hooks, changedAsset, command, ClaudeStyleEvent(target.Provider, asset)),
                    setVersion: false);
                break;

            case ProviderName.Cursor:
                MergeSharedConfig(targetPath, asset, backupIfExists,
                    (hooks, changedAsset) => MergeCursorHook(hooks, changedAsset, command),
                    setVersion: true);
                break;

            case ProviderName.Copilot:
                WriteCopilotHookFile(targetPath, asset, command, supportPath, backupIfExists);
                break;

            default:
                throw new AgentPackException($"{target.Provider.Display()} hook installation is not implemented.");
        }

        return ContentHash.Compute(targetPath);
    }

    /// <summary>The exact config fragment that will be written — used for previews before applying.</summary>
    public static string Preview(Asset asset, InstallTarget target, string scopeRoot)
    {
        var command = "./" + SupportRelativePath(target.Provider, asset.Id, InstallScope.Project).Replace(Path.DirectorySeparatorChar, '/')
                      + "/" + (asset.Hook?.Command ?? "hook.sh");

        JsonObject fragment = target.Provider switch
        {
            ProviderName.Cursor => new JsonObject
            {
                ["hooks"] = new JsonObject { [CursorEvent(asset)] = new JsonArray { CursorEntry(asset, command) } }
            },
            ProviderName.Copilot => CopilotHookDocument(asset, command, powershellCommand: null),
            _ => new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    [ClaudeStyleEvent(target.Provider, asset)] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["matcher"] = Matcher(asset),
                            ["hooks"] = new JsonArray { ClaudeStyleHandler(asset, command) }
                        }
                    }
                }
            }
        };

        return fragment.ToJsonString(JsonOptions);
    }

    /// <summary>Where the hook's executable content lives, per provider and scope.</summary>
    public static string SupportRelativePath(ProviderName provider, string assetId, InstallScope scope) => provider switch
    {
        ProviderName.Claude => Path.Combine(".claude", "hooks", assetId),
        ProviderName.Codex => Path.Combine(".codex", "hooks", assetId),
        ProviderName.Cursor => Path.Combine(".cursor", "hooks", assetId),
        ProviderName.Copilot => scope == InstallScope.User
            ? Path.Combine(".copilot", "hooks", assetId)
            : Path.Combine(".github", "hooks", assetId),
        _ => throw new AgentPackException($"{provider.Display()} hook installation is not implemented.")
    };

    /// <summary>Copilot hook config files are per-asset, so removal may delete them; the other providers share one config file.</summary>
    public static bool IsSharedConfigFile(ProviderName provider) => provider != ProviderName.Copilot;

    private static void MergeSharedConfig(string path, Asset asset, Action<string> backupIfExists, Func<JsonObject, Asset, bool> merge, bool setVersion)
    {
        var root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        if (setVersion) root["version"] ??= 1;
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var changed = merge(hooks, asset);
        if (!changed && File.Exists(path)) return;
        if (File.Exists(path)) backupIfExists(path);
        AtomicWrite.Text(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static bool MergeClaudeStyleHook(JsonObject hooks, Asset asset, string command, string eventName)
    {
        var matcher = Matcher(asset);
        var handler = ClaudeStyleHandler(asset, command);

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

    private static bool MergeCursorHook(JsonObject hooks, Asset asset, string command)
    {
        var eventName = CursorEvent(asset);
        var entry = CursorEntry(asset, command);
        if (hooks[eventName] is not JsonArray eventArray)
        {
            hooks[eventName] = new JsonArray { entry };
            return true;
        }

        if (eventArray.Any(existing => JsonNode.DeepEquals(existing, entry))) return false;
        eventArray.Add(entry);
        return true;
    }

    private static void WriteCopilotHookFile(string path, Asset asset, string command, string supportPath, Action<string> backupIfExists)
    {
        // A PowerShell twin next to the bash script makes the hook cross-platform.
        var powershell = FindPowershellTwin(supportPath, command);
        var document = CopilotHookDocument(asset, command, powershell);

        if (File.Exists(path))
        {
            var existing = JsonNode.Parse(File.ReadAllText(path));
            if (JsonNode.DeepEquals(existing, document)) return;
            backupIfExists(path);
        }

        AtomicWrite.Text(path, document.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static JsonObject CopilotHookDocument(Asset asset, string bashCommand, string? powershellCommand)
    {
        var entry = new JsonObject
        {
            ["type"] = "command",
            ["bash"] = bashCommand,
            ["timeoutSec"] = asset.Hook?.TimeoutSec ?? 30
        };
        if (powershellCommand is not null) entry["powershell"] = powershellCommand;

        return new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject { [CopilotEvent(asset)] = new JsonArray { entry } }
        };
    }

    private static string? FindPowershellTwin(string supportPath, string bashCommand)
    {
        if (!Directory.Exists(supportPath)) return null;
        var twinName = Path.GetFileNameWithoutExtension(bashCommand) + ".ps1";
        return Directory.EnumerateFiles(supportPath, "*.ps1", SearchOption.AllDirectories)
            .Any(f => Path.GetFileName(f).Equals(twinName, StringComparison.OrdinalIgnoreCase))
            ? Path.ChangeExtension(bashCommand, ".ps1")
            : null;
    }

    private static JsonObject ClaudeStyleHandler(Asset asset, string command) => new()
    {
        ["type"] = "command",
        ["command"] = command,
        ["timeout"] = asset.Hook?.TimeoutSec ?? 30
    };

    private static JsonObject CursorEntry(Asset asset, string command)
    {
        var entry = new JsonObject
        {
            ["command"] = command,
            ["timeout"] = asset.Hook?.TimeoutSec ?? 30
        };
        if (!string.IsNullOrWhiteSpace(asset.Hook?.Tool)) entry["matcher"] = asset.Hook.Tool;
        return entry;
    }

    private static string ClaudeStyleEvent(ProviderName provider, Asset asset)
    {
        var trigger = Trigger(asset);
        if (provider == ProviderName.Codex && trigger == HookTrigger.Notification)
        {
            throw new AgentPackException(
                "Codex hooks do not support the notification trigger.",
                "Supported for Codex: preToolUse, postToolUse, stop, sessionStart, userPromptSubmit.");
        }

        // Claude Code and Codex share the PascalCase event vocabulary.
        return trigger.ToString();
    }

    private static string CursorEvent(Asset asset) => Trigger(asset) switch
    {
        HookTrigger.PreToolUse => "preToolUse",
        HookTrigger.PostToolUse => "postToolUse",
        HookTrigger.Stop => "stop",
        HookTrigger.SessionStart => "sessionStart",
        HookTrigger.UserPromptSubmit => "beforeSubmitPrompt",
        var trigger => throw new AgentPackException(
            $"Cursor hooks do not support the {EnumParsers.CamelCase(trigger.ToString())} trigger.",
            "Supported for Cursor: preToolUse, postToolUse, stop, sessionStart, userPromptSubmit.")
    };

    private static string CopilotEvent(Asset asset) => Trigger(asset) switch
    {
        HookTrigger.PreToolUse => "preToolUse",
        HookTrigger.PostToolUse => "postToolUse",
        HookTrigger.Stop => "agentStop",
        HookTrigger.SessionStart => "sessionStart",
        HookTrigger.UserPromptSubmit => "userPromptSubmitted",
        HookTrigger.Notification => "notification",
        var trigger => throw new AgentPackException(
            $"Copilot hooks do not support the {EnumParsers.CamelCase(trigger.ToString())} trigger.")
    };

    private static HookTrigger Trigger(Asset asset) => asset.Hook?.Trigger ?? HookTrigger.PreToolUse;

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
