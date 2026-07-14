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
        var powershellCommandPath = target.Provider == ProviderName.Copilot
            ? FindPowershellTwinPath(supportPath, commandPath)
            : null;
        if (target.Provider == ProviderName.Copilot && Trigger(asset) == HookTrigger.PreToolUse)
        {
            (commandPath, powershellCommandPath) = CreateCopilotPreToolWrappers(
                supportPath, commandPath, powershellCommandPath);
        }

        var command = ConfigCommand(commandPath, scopeRoot, scope);
        var powershellCommand = powershellCommandPath is null
            ? null
            : ConfigCommand(powershellCommandPath, scopeRoot, scope);

        switch (target.Provider)
        {
            case ProviderName.Claude:
            case ProviderName.Codex:
                MergeSharedConfig(targetPath, backupIfExists, hooks =>
                {
                    RemoveManagedRegistrations(hooks, target.Provider, supportPath);
                    MergeClaudeStyleHook(hooks, asset, command, ClaudeStyleEvent(target.Provider, asset));
                },
                    setVersion: false);
                break;

            case ProviderName.Cursor:
                MergeSharedConfig(targetPath, backupIfExists, hooks =>
                {
                    RemoveManagedRegistrations(hooks, target.Provider, supportPath);
                    MergeCursorHook(hooks, asset, command);
                },
                    setVersion: true);
                break;

            case ProviderName.Copilot:
                WriteCopilotHookFile(targetPath, asset, command, powershellCommand, backupIfExists);
                break;

            default:
                throw new AgentPackException($"{target.Provider.Display()} hook installation is not implemented.");
        }

        return CurrentChecksum(asset, targetPath, target.Provider, scopeRoot, scope);
    }

    /// <summary>
    /// Hashes only this asset's registration inside a shared hook file. Unrelated
    /// user hooks and other AgentPack hooks must not make this asset look modified.
    /// </summary>
    public static string CurrentChecksum(
        Asset asset,
        string targetPath,
        ProviderName provider,
        string scopeRoot,
        InstallScope scope) => CurrentChecksum(asset.Id, targetPath, provider, scopeRoot, scope);

    public static string CurrentChecksum(
        string assetId,
        string targetPath,
        ProviderName provider,
        string scopeRoot,
        InstallScope scope)
    {
        if (!File.Exists(targetPath)) return "";
        if (provider == ProviderName.Copilot) return ContentHash.Compute(targetPath);

        var root = ParseRoot(targetPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        var supportPath = Path.GetFullPath(Path.Combine(scopeRoot, SupportRelativePath(provider, assetId, scope)));
        var fragment = ManagedFragment(hooks, provider, supportPath);
        return ContentHash.ComputeText(fragment.ToJsonString(JsonOptions));
    }

    /// <summary>Removes only this asset's entries from a shared provider config.</summary>
    public static void Remove(
        string assetId,
        ProviderName provider,
        string targetPath,
        string scopeRoot,
        InstallScope scope,
        Action<string> backupIfExists)
    {
        if (provider == ProviderName.Copilot || !File.Exists(targetPath)) return;
        var root = ParseRoot(targetPath);
        if (root["hooks"] is not JsonObject hooks) return;

        var supportPath = Path.GetFullPath(Path.Combine(scopeRoot, SupportRelativePath(provider, assetId, scope)));
        if (!RemoveManagedRegistrations(hooks, provider, supportPath)) return;

        backupIfExists(targetPath);
        AtomicWrite.Text(targetPath, root.ToJsonString(JsonOptions) + Environment.NewLine);
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

    private static void MergeSharedConfig(string path, Action<string> backupIfExists, Action<JsonObject> merge, bool setVersion)
    {
        var root = File.Exists(path)
            ? ParseRoot(path)
            : new JsonObject();
        var before = root.DeepClone();

        if (setVersion) root["version"] ??= 1;
        if (root["hooks"] is not JsonObject hooks)
        {
            if (root["hooks"] is not null)
            {
                throw new AgentPackException(
                    $"Hook configuration '{path}' has a non-object 'hooks' value.",
                    "Change 'hooks' to a JSON object and retry; AgentPack did not overwrite it.",
                    ExitCodes.DriftOrConflict);
            }
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        merge(hooks);
        if (JsonNode.DeepEquals(before, root) && File.Exists(path)) return;
        if (File.Exists(path)) backupIfExists(path);
        AtomicWrite.Text(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static JsonObject ManagedFragment(JsonObject hooks, ProviderName provider, string supportPath)
    {
        var selectedHooks = new JsonObject();
        foreach (var (eventName, eventNode) in hooks)
        {
            if (eventNode is not JsonArray eventArray) continue;
            var selectedEvent = new JsonArray();

            if (provider is ProviderName.Claude or ProviderName.Codex)
            {
                foreach (var group in eventArray.OfType<JsonObject>())
                {
                    if (group["hooks"] is not JsonArray handlers) continue;
                    var selectedHandlers = new JsonArray();
                    foreach (var handler in handlers.OfType<JsonObject>().Where(x => BelongsToAsset(x, supportPath)))
                        selectedHandlers.Add(handler.DeepClone());
                    if (selectedHandlers.Count == 0) continue;
                    selectedEvent.Add(new JsonObject
                    {
                        ["matcher"] = group["matcher"]?.DeepClone(),
                        ["hooks"] = selectedHandlers
                    });
                }
            }
            else
            {
                foreach (var entry in eventArray.OfType<JsonObject>().Where(x => BelongsToAsset(x, supportPath)))
                    selectedEvent.Add(entry.DeepClone());
            }

            if (selectedEvent.Count > 0) selectedHooks[eventName] = selectedEvent;
        }

        return new JsonObject { ["hooks"] = selectedHooks };
    }

    private static bool RemoveManagedRegistrations(JsonObject hooks, ProviderName provider, string supportPath)
    {
        var changed = false;
        foreach (var (eventName, eventNode) in hooks.ToList())
        {
            if (eventNode is not JsonArray eventArray) continue;

            for (var index = eventArray.Count - 1; index >= 0; index--)
            {
                if (eventArray[index] is not JsonObject entry) continue;
                if (provider is ProviderName.Claude or ProviderName.Codex)
                {
                    if (entry["hooks"] is not JsonArray handlers) continue;
                    for (var handlerIndex = handlers.Count - 1; handlerIndex >= 0; handlerIndex--)
                    {
                        if (handlers[handlerIndex] is JsonObject handler && BelongsToAsset(handler, supportPath))
                        {
                            handlers.RemoveAt(handlerIndex);
                            changed = true;
                        }
                    }
                    if (handlers.Count == 0) eventArray.RemoveAt(index);
                }
                else if (BelongsToAsset(entry, supportPath))
                {
                    eventArray.RemoveAt(index);
                    changed = true;
                }
            }

            if (eventArray.Count == 0) hooks.Remove(eventName);
        }

        return changed;
    }

    private static bool BelongsToAsset(JsonObject entry, string supportPath)
    {
        var command = entry["command"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(command)) return false;
        var normalizedCommand = command.Replace('\\', '/');
        var normalizedSupport = supportPath.Replace('\\', '/').TrimEnd('/') + "/";
        if (normalizedCommand.StartsWith(normalizedSupport, StringComparison.OrdinalIgnoreCase)) return true;

        var directoryName = Path.GetFileName(supportPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var providerDirectory = Path.GetFileName(Path.GetDirectoryName(supportPath));
        return normalizedCommand.Contains($"/{providerDirectory}/{directoryName}/", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ParseRoot(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidOperationException("the JSON root must be an object");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new AgentPackException(
                $"Hook configuration '{path}' is not a valid JSON object: {ex.Message}",
                "Fix the JSON syntax and retry; AgentPack did not change the file.",
                ExitCodes.ValidationFailed);
        }
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

    private static void WriteCopilotHookFile(
        string path,
        Asset asset,
        string command,
        string? powershellCommand,
        Action<string> backupIfExists)
    {
        var document = CopilotHookDocument(asset, command, powershellCommand);

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

    private static string? FindPowershellTwinPath(string supportPath, string commandPath)
    {
        if (!Directory.Exists(supportPath)) return null;
        var twinName = Path.GetFileNameWithoutExtension(commandPath) + ".ps1";
        return Directory.EnumerateFiles(supportPath, "*.ps1", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(twinName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copilot treats exit 2 as a warning for preToolUse, while Claude, Codex,
    /// and Cursor treat it as a denial. Adapt the portable exit-2 contract to
    /// Copilot's structured permissionDecision output without changing assets.
    /// </summary>
    private static (string Bash, string? Powershell) CreateCopilotPreToolWrappers(
        string supportPath,
        string commandPath,
        string? powershellCommandPath)
    {
        var relativeCommand = Path.GetRelativePath(supportPath, commandPath)
            .Replace(Path.DirectorySeparatorChar, '/').Replace("\"", "\\\"");
        var bashWrapper = Path.Combine(supportPath, ".agentpack-copilot-pre-tool.sh");
        AtomicWrite.Text(bashWrapper, $$"""
            #!/usr/bin/env bash
            set +e
            script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
            output="$("$script_dir/{{relativeCommand}}" "$@")"
            status=$?
            if [ "$status" -eq 2 ]; then
              printf '%s\n' '{"permissionDecision":"deny","permissionDecisionReason":"Blocked by AgentPack policy hook."}'
              exit 0
            fi
            printf '%s\n' "$output"
            exit "$status"
            """ + Environment.NewLine);
        ContentHash.MakeExecutable(bashWrapper);

        if (powershellCommandPath is null) return (bashWrapper, null);

        var relativePowershell = Path.GetRelativePath(supportPath, powershellCommandPath)
            .Replace(Path.DirectorySeparatorChar, '/').Replace("'", "''");
        var powershellWrapper = Path.Combine(supportPath, ".agentpack-copilot-pre-tool.ps1");
        AtomicWrite.Text(powershellWrapper, $$"""
            $script = Join-Path $PSScriptRoot '{{relativePowershell}}'
            $payload = [Console]::In.ReadToEnd()
            $output = $payload | & $script @args
            $status = $LASTEXITCODE
            if ($status -eq 2) {
              Write-Output '{"permissionDecision":"deny","permissionDecisionReason":"Blocked by AgentPack policy hook."}'
              exit 0
            }
            $output | Write-Output
            exit $status
            """ + Environment.NewLine);
        return (bashWrapper, powershellWrapper);
    }

    private static string ConfigCommand(string path, string scopeRoot, InstallScope scope) => scope == InstallScope.User
        ? path
        : "./" + Path.GetRelativePath(scopeRoot, path).Replace(Path.DirectorySeparatorChar, '/');

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
            var command = PathSafety.ResolveUnderRoot(supportPath, asset.Hook.Command, $"Hook asset '{asset.Id}' command");
            if (!File.Exists(command))
            {
                throw new AgentPackException(
                    $"Hook asset '{asset.Id}' command '{asset.Hook.Command}' was not found in its content.",
                    "Add the command file to the hook content directory and retry.",
                    ExitCodes.ValidationFailed);
            }
            return command;
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
