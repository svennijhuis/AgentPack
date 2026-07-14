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

    /// <summary>
    /// Installs the hook content and registers it in the provider config.
    /// <paramref name="previousFragment"/> is the registration recorded at the last
    /// install (so upgrades replace it instead of stacking duplicates), and
    /// <paramref name="overwriteModified"/> is the user's drift decision for
    /// registrations that were edited after install.
    /// </summary>
    public static MergeResult Apply(Asset asset, string sourcePath, InstallTarget target, string targetPath, string scopeRoot, InstallScope scope,
        Action<string> backupIfExists, string? previousFragment = null, bool overwriteModified = false)
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
        var powershell = powershellCommandPath is null
            ? null
            : ConfigCommand(powershellCommandPath, scopeRoot, scope);

        string fragment;
        switch (target.Provider)
        {
            case ProviderName.Claude:
            case ProviderName.Codex:
                {
                    var eventName = ClaudeStyleEvent(target.Provider, asset);
                    var handler = ClaudeStyleHandler(asset, command);
                    fragment = ClaudeStyleFragment(eventName, Matcher(asset), handler);
                    MergeSharedConfig(targetPath, backupIfExists,
                        hooks => MergeClaudeStyleHook(hooks, targetPath, Matcher(asset), handler, eventName, previousFragment, overwriteModified),
                        setVersion: false);
                    break;
                }

            case ProviderName.Cursor:
                {
                    var entry = CursorEntry(asset, command);
                    fragment = CursorFragment(CursorEvent(asset), entry);
                    MergeSharedConfig(targetPath, backupIfExists,
                        hooks => MergeCursorHook(hooks, targetPath, CursorEvent(asset), entry, previousFragment, overwriteModified),
                        setVersion: true);
                    break;
                }

            case ProviderName.Copilot:
                {
                    var document = CopilotHookDocument(asset, command, powershell);
                    fragment = document.ToJsonString();
                    WriteCopilotHookFile(targetPath, document, backupIfExists);
                    break;
                }

            default:
                throw new AgentPackException($"{target.Provider.Display()} hook installation is not implemented.");
        }

        return new MergeResult(ContentHash.OfText(fragment), fragment);
    }

    /// <summary>
    /// The fragment that installing this asset would record, without writing anything.
    /// Requires the hook's support content to already be on disk (it identifies the
    /// command script). Used to backfill lock entries written by agentpack &lt; 1.0.
    /// </summary>
    public static string ComputeFragment(Asset asset, InstallTarget target, string scopeRoot, InstallScope scope)
    {
        var supportPath = Path.GetFullPath(Path.Combine(scopeRoot, SupportRelativePath(target.Provider, asset.Id, scope)));
        var commandPath = ResolveHookCommand(asset, supportPath);
        var powershellCommandPath = target.Provider == ProviderName.Copilot
            ? FindPowershellTwinPath(supportPath, commandPath)
            : null;
        if (target.Provider == ProviderName.Copilot && Trigger(asset) == HookTrigger.PreToolUse)
        {
            var bashWrapper = Path.Combine(supportPath, ".agentpack-copilot-pre-tool.sh");
            var powershellWrapper = Path.Combine(supportPath, ".agentpack-copilot-pre-tool.ps1");
            if (File.Exists(bashWrapper)) commandPath = bashWrapper;
            if (File.Exists(powershellWrapper)) powershellCommandPath = powershellWrapper;
        }

        var command = ConfigCommand(commandPath, scopeRoot, scope);
        var powershell = powershellCommandPath is null
            ? null
            : ConfigCommand(powershellCommandPath, scopeRoot, scope);

        return target.Provider switch
        {
            ProviderName.Claude or ProviderName.Codex =>
                ClaudeStyleFragment(ClaudeStyleEvent(target.Provider, asset), Matcher(asset), ClaudeStyleHandler(asset, command)),
            ProviderName.Cursor => CursorFragment(CursorEvent(asset), CursorEntry(asset, command)),
            ProviderName.Copilot => CopilotHookDocument(asset, command, powershell).ToJsonString(),
            _ => throw new AgentPackException($"{target.Provider.Display()} hook installation is not implemented.")
        };
    }

    /// <summary>Is the registration we installed still in the provider config, and unchanged?</summary>
    public static FragmentState CheckFragment(string targetPath, ProviderName provider, string fragment)
    {
        if (!File.Exists(targetPath)) return FragmentState.Absent;

        JsonObject root;
        try
        {
            root = UserConfigJson.LoadObject(targetPath);
        }
        catch (AgentPackException)
        {
            return FragmentState.Modified;
        }

        var stored = UserConfigJson.ParseFragment(fragment);
        switch (provider)
        {
            case ProviderName.Copilot:
                return JsonNode.DeepEquals(root, stored) ? FragmentState.Present : FragmentState.Modified;

            case ProviderName.Cursor:
                {
                    var entry = stored["entry"] as JsonObject;
                    var existing = FindCursorEntry(root["hooks"] as JsonObject, stored["event"]?.GetValue<string>() ?? "", CommandOf(entry));
                    if (existing is null) return FragmentState.Absent;
                    return JsonNode.DeepEquals(existing, entry) ? FragmentState.Present : FragmentState.Modified;
                }

            default:
                {
                    var handler = stored["handler"] as JsonObject;
                    var existing = FindClaudeStyleHandler(root["hooks"] as JsonObject, stored["event"]?.GetValue<string>() ?? "", CommandOf(handler));
                    if (existing is null) return FragmentState.Absent;
                    return JsonNode.DeepEquals(existing.Value.Handler, handler) &&
                           MatcherEquals(existing.Value.Matcher, stored["matcher"]?.GetValue<string>())
                        ? FragmentState.Present
                        : FragmentState.Modified;
                }
        }
    }

    /// <summary>Removes our registration from the shared provider config (used by 'agentpack remove').</summary>
    public static void RemoveFragment(string targetPath, ProviderName provider, string fragment, Action<string> backupIfExists)
    {
        if (!File.Exists(targetPath) || provider == ProviderName.Copilot) return;

        var root = UserConfigJson.LoadObject(targetPath);
        var stored = UserConfigJson.ParseFragment(fragment);
        var hooks = root["hooks"] as JsonObject;
        var changed = provider == ProviderName.Cursor
            ? RemoveCursorEntry(hooks, stored["event"]?.GetValue<string>() ?? "", CommandOf(stored["entry"] as JsonObject))
            : RemoveClaudeStyleHandler(hooks, stored["event"]?.GetValue<string>() ?? "", CommandOf(stored["handler"] as JsonObject));

        if (!changed) return;
        backupIfExists(targetPath);
        AtomicWrite.Text(targetPath, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    /// <summary>The exact config fragment that will be written — used for previews before applying.</summary>
    public static string Preview(Asset asset, InstallTarget target, string scopeRoot)
    {
        var support = "./" + SupportRelativePath(target.Provider, asset.Id, InstallScope.Project)
            .Replace(Path.DirectorySeparatorChar, '/');
        var command = support + "/" + (asset.Hook?.Command ?? "hook.sh");
        if (target.Provider == ProviderName.Copilot && Trigger(asset) == HookTrigger.PreToolUse)
            command = support + "/.agentpack-copilot-pre-tool.sh";

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

    private static void MergeSharedConfig(string path, Action<string> backupIfExists, Func<JsonObject, bool> merge, bool setVersion)
    {
        var root = UserConfigJson.LoadObject(path);

        if (setVersion) root["version"] ??= 1;
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var changed = merge(hooks);
        if (!changed && File.Exists(path)) return;
        if (File.Exists(path)) backupIfExists(path);
        AtomicWrite.Text(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static bool MergeClaudeStyleHook(JsonObject hooks, string path, string matcher, JsonObject handler, string eventName,
        string? previousFragment, bool overwriteModified)
    {
        var changed = ReplaceableClaudeStyleHandler(hooks, path, eventName, handler, previousFragment, overwriteModified);

        if (hooks[eventName] is not JsonArray eventArray)
        {
            hooks[eventName] = new JsonArray
            {
                new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray { CloneNode(handler) } }
            };
            return true;
        }

        foreach (var node in eventArray.OfType<JsonObject>())
        {
            if (!matcher.Equals(node["matcher"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)) continue;
            if (node["hooks"] is not JsonArray handlers)
            {
                node["hooks"] = new JsonArray { CloneNode(handler) };
                return true;
            }

            if (handlers.Any(existing => JsonNode.DeepEquals(existing, handler))) return changed;
            handlers.Add(CloneNode(handler));
            return true;
        }

        eventArray.Add(new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray { CloneNode(handler) } });
        return true;
    }

    /// <summary>
    /// Clears the way for a re-registration: removes our previous registration
    /// (recorded in the lockfile) and, when the drift decision allows it, a
    /// locally modified registration with the same command. A same-command
    /// registration we cannot account for is a conflict, never silently stacked.
    /// </summary>
    private static bool ReplaceableClaudeStyleHandler(JsonObject hooks, string path, string eventName, JsonObject handler,
        string? previousFragment, bool overwriteModified)
    {
        var changed = false;
        if (previousFragment is not null)
        {
            var previous = UserConfigJson.ParseFragment(previousFragment);
            changed = RemoveClaudeStyleHandler(hooks, previous["event"]?.GetValue<string>() ?? "", CommandOf(previous["handler"] as JsonObject));
        }

        var command = CommandOf(handler);
        var existing = FindClaudeStyleHandler(hooks, eventName, command);
        if (existing is null || JsonNode.DeepEquals(existing.Value.Handler, handler)) return changed;

        if (!overwriteModified)
        {
            throw new AgentPackException(
                $"A hook running '{command}' already exists in {path} with a different configuration.",
                "Remove the existing entry (or rerun with --force to overwrite), then retry.",
                ExitCodes.DriftOrConflict);
        }

        return RemoveClaudeStyleHandler(hooks, eventName, command) || changed;
    }

    private static bool MergeCursorHook(JsonObject hooks, string path, string eventName, JsonObject entry,
        string? previousFragment, bool overwriteModified)
    {
        var changed = false;
        if (previousFragment is not null)
        {
            var previous = UserConfigJson.ParseFragment(previousFragment);
            changed = RemoveCursorEntry(hooks, previous["event"]?.GetValue<string>() ?? "", CommandOf(previous["entry"] as JsonObject));
        }

        var command = CommandOf(entry);
        var existing = FindCursorEntry(hooks, eventName, command);
        if (existing is not null && !JsonNode.DeepEquals(existing, entry))
        {
            if (!overwriteModified)
            {
                throw new AgentPackException(
                    $"A hook running '{command}' already exists in {path} with a different configuration.",
                    "Remove the existing entry (or rerun with --force to overwrite), then retry.",
                    ExitCodes.DriftOrConflict);
            }

            changed = RemoveCursorEntry(hooks, eventName, command) || changed;
        }

        if (hooks[eventName] is not JsonArray eventArray)
        {
            hooks[eventName] = new JsonArray { CloneNode(entry) };
            return true;
        }

        if (eventArray.Any(x => JsonNode.DeepEquals(x, entry))) return changed;
        eventArray.Add(CloneNode(entry));
        return true;
    }

    private static void WriteCopilotHookFile(string path, JsonObject document, Action<string> backupIfExists)
    {
        if (File.Exists(path))
        {
            var existing = UserConfigJson.LoadObject(path);
            if (JsonNode.DeepEquals(existing, document)) return;
            backupIfExists(path);
        }

        AtomicWrite.Text(path, document.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static (JsonObject Handler, string? Matcher)? FindClaudeStyleHandler(JsonObject? hooks, string eventName, string command)
    {
        if (command.Length == 0 || hooks?[eventName] is not JsonArray eventArray) return null;
        foreach (var group in eventArray.OfType<JsonObject>())
        {
            if (group["hooks"] is not JsonArray handlers) continue;
            foreach (var candidate in handlers.OfType<JsonObject>())
            {
                if (command.Equals(CommandOf(candidate), StringComparison.Ordinal))
                {
                    return (candidate, group["matcher"]?.GetValue<string>());
                }
            }
        }

        return null;
    }

    private static bool RemoveClaudeStyleHandler(JsonObject? hooks, string eventName, string command)
    {
        if (command.Length == 0 || hooks?[eventName] is not JsonArray eventArray) return false;

        var changed = false;
        foreach (var group in eventArray.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is not JsonArray handlers) continue;
            foreach (var candidate in handlers.OfType<JsonObject>().ToList())
            {
                if (command.Equals(CommandOf(candidate), StringComparison.Ordinal))
                {
                    handlers.Remove(candidate);
                    changed = true;
                }
            }

            if (handlers.Count == 0) eventArray.Remove(group);
        }

        if (eventArray.Count == 0) hooks.Remove(eventName);
        return changed;
    }

    private static JsonObject? FindCursorEntry(JsonObject? hooks, string eventName, string command)
    {
        if (command.Length == 0 || hooks?[eventName] is not JsonArray eventArray) return null;
        return eventArray.OfType<JsonObject>().FirstOrDefault(x => command.Equals(CommandOf(x), StringComparison.Ordinal));
    }

    private static bool RemoveCursorEntry(JsonObject? hooks, string eventName, string command)
    {
        if (command.Length == 0 || hooks?[eventName] is not JsonArray eventArray) return false;

        var changed = false;
        foreach (var candidate in eventArray.OfType<JsonObject>().ToList())
        {
            if (command.Equals(CommandOf(candidate), StringComparison.Ordinal))
            {
                eventArray.Remove(candidate);
                changed = true;
            }
        }

        if (eventArray.Count == 0) hooks.Remove(eventName);
        return changed;
    }

    private static string CommandOf(JsonObject? node) => node?["command"]?.GetValue<string>() ?? "";

    private static string ClaudeStyleFragment(string eventName, string matcher, JsonObject handler) =>
        new JsonObject { ["event"] = eventName, ["matcher"] = matcher, ["handler"] = CloneNode(handler) }.ToJsonString();

    private static string CursorFragment(string eventName, JsonObject entry) =>
        new JsonObject { ["event"] = eventName, ["entry"] = CloneNode(entry) }.ToJsonString();

    private static bool MatcherEquals(string? actual, string? expected) =>
        string.Equals(actual ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase);

    private static JsonObject CloneNode(JsonObject node) => JsonNode.Parse(node.ToJsonString())!.AsObject();

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
