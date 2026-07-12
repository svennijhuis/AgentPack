using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentPack.Core;

/// <summary>
/// Merges MCP server entries into provider config files without disturbing
/// anything the user already has there. JSON providers differ only in the root
/// key (.vscode/mcp.json uses "servers", everything else "mcpServers");
/// Codex uses TOML [mcp_servers.*] sections.
/// </summary>
public static class McpMerger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex BareTomlKey = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Merges the asset's servers into the provider config. <paramref name="previousFragment"/>
    /// is the fragment recorded at the last install (so upgrades replace our old entry), and
    /// <paramref name="overwriteModified"/> is the user's drift decision for entries that were
    /// edited after install. Anything else that already exists with different content is a conflict.
    /// </summary>
    public static MergeResult Apply(Asset asset, string? sourcePath, InstallTarget target, string targetPath, InstallScope scope,
        Action<string> backupIfExists, string? previousFragment = null, bool overwriteModified = false)
    {
        var servers = BuildServers(asset, sourcePath, target.Provider, scope);
        string fragment;
        if (target.Provider == ProviderName.Codex)
        {
            fragment = TomlFragment(servers);
            MergeCodexToml(targetPath, servers, previousFragment, overwriteModified, backupIfExists);
        }
        else
        {
            fragment = servers.ToJsonString();
            MergeJson(targetPath, RootKey(target.Provider, scope), servers, previousFragment, overwriteModified, backupIfExists);
        }

        return new MergeResult(ContentHash.OfText(fragment), fragment);
    }

    /// <summary>Is the fragment we installed still in the config file, and unchanged?</summary>
    public static FragmentState CheckFragment(string targetPath, ProviderName provider, InstallScope scope, string fragment)
    {
        if (!File.Exists(targetPath)) return FragmentState.Absent;
        if (provider == ProviderName.Codex) return CheckToml(targetPath, fragment);

        JsonObject root;
        try
        {
            root = UserConfigJson.LoadObject(targetPath);
        }
        catch (AgentPackException)
        {
            return FragmentState.Modified;
        }

        var servers = root[RootKey(provider, scope)] as JsonObject;
        var states = UserConfigJson.ParseFragment(fragment)
            .Select(pair => servers?[pair.Key] is not JsonNode existing
                ? FragmentState.Absent
                : JsonNode.DeepEquals(existing, pair.Value) ? FragmentState.Present : FragmentState.Modified)
            .ToList();

        if (states.Count == 0 || states.All(x => x == FragmentState.Absent)) return FragmentState.Absent;
        return states.All(x => x == FragmentState.Present) ? FragmentState.Present : FragmentState.Modified;
    }

    /// <summary>Removes our servers from the config file (used by 'agentpack remove').</summary>
    public static void RemoveFragment(string targetPath, ProviderName provider, InstallScope scope, string fragment, Action<string> backupIfExists)
    {
        if (!File.Exists(targetPath)) return;
        if (provider == ProviderName.Codex)
        {
            RemoveToml(targetPath, fragment, backupIfExists);
            return;
        }

        var root = UserConfigJson.LoadObject(targetPath);
        if (root[RootKey(provider, scope)] is not JsonObject servers) return;

        var changed = false;
        foreach (var serverId in UserConfigJson.ParseFragment(fragment).Select(x => x.Key).ToList())
        {
            if (servers.ContainsKey(serverId))
            {
                servers.Remove(serverId);
                changed = true;
            }
        }

        if (!changed) return;
        backupIfExists(targetPath);
        AtomicWrite.Text(targetPath, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    /// <summary>The exact fragment that will be merged — used for previews before applying.</summary>
    public static string Preview(Asset asset, string? sourcePath, InstallTarget target, InstallScope scope)
    {
        var servers = BuildServers(asset, sourcePath, target.Provider, scope);
        if (target.Provider == ProviderName.Codex)
        {
            var builder = new StringBuilder();
            foreach (var (serverId, serverNode) in servers)
            {
                if (serverNode is JsonObject server) builder.AppendLine(BuildCodexTomlBlock(serverId, server).TrimEnd());
            }

            return builder.ToString().TrimEnd();
        }

        return new JsonObject { [RootKey(target.Provider, scope)] = Clone(servers) }.ToJsonString(JsonOptions);
    }

    public static string RootKey(ProviderName provider, InstallScope scope) =>
        provider == ProviderName.Copilot && scope == InstallScope.Project ? "servers" : "mcpServers";

    public static JsonObject BuildServers(Asset asset, string? sourcePath, ProviderName provider, InstallScope scope)
    {
        if (asset.Mcp is not null)
        {
            return new JsonObject { [asset.Mcp.Server] = BuildTypedServer(asset.Mcp, provider, scope) };
        }

        var rawPath = FindMcpJson(sourcePath);
        if (rawPath is null)
        {
            throw new AgentPackException(
                $"MCP asset '{asset.Id}' must define mcp: metadata or ship a content/mcp.json.",
                $"Add an mcp: section to assets/mcp/{asset.Id}/agentpack.yaml.");
        }

        var root = JsonNode.Parse(File.ReadAllText(rawPath))?.AsObject()
            ?? throw new AgentPackException($"MCP asset '{asset.Id}' has an invalid mcp.json.");

        if (root["mcpServers"] is JsonObject existingServers)
        {
            return Clone(existingServers).AsObject();
        }

        var serverName = StringValue(root, "server") ?? StringValue(root, "name") ?? asset.Id;
        return new JsonObject { [serverName] = BuildRawServer(root, asset.Id, provider, scope) };
    }

    /// <summary>
    /// How each target references environment variables — verified per product:
    /// Claude Code expands ${VAR}; VS Code and Cursor expand ${env:VAR};
    /// Copilot CLI documents no expansion (stdio servers inherit the shell env, so the
    /// env object is omitted); Codex converts to env_vars/env_http_headers in TOML.
    /// </summary>
    private static string EnvPlaceholder(ProviderName provider, InstallScope scope, string envVar) => provider switch
    {
        ProviderName.Cursor => "${env:" + envVar + "}",
        ProviderName.Copilot when scope == InstallScope.Project => "${env:" + envVar + "}",
        _ => "${" + envVar + "}"
    };

    private static bool OmitEnvObject(ProviderName provider, InstallScope scope) =>
        provider == ProviderName.Copilot && scope == InstallScope.User;

    private static JsonObject BuildTypedServer(McpServer mcp, ProviderName provider, InstallScope scope)
    {
        var server = new JsonObject { ["type"] = TransportName(mcp.Transport) };
        if (mcp.Transport == McpTransport.Stdio)
        {
            server["command"] = mcp.Command ?? "";
            server["args"] = ToJsonArray(mcp.Args);
            if (mcp.Cwd is not null) server["cwd"] = mcp.Cwd;
            if (!OmitEnvObject(provider, scope))
            {
                server["env"] = EnvObject(mcp.EnvVars, provider, scope);
            }
        }
        else
        {
            server["url"] = mcp.Url ?? "";
            if (mcp.HeaderEnvVars.Count > 0)
            {
                var headers = new JsonObject();
                foreach (var (header, envVar) in mcp.HeaderEnvVars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    headers[header] = EnvPlaceholder(provider, scope, envVar);
                }

                server["headers"] = headers;
            }
        }

        var tools = mcp.Tools.Where(x => !string.IsNullOrWhiteSpace(x) && x != "*").ToList();
        if (tools.Count > 0)
        {
            server["tools"] = ToJsonArray(tools);
        }
        else if (provider == ProviderName.Copilot && scope == InstallScope.User)
        {
            // Copilot CLI expects an explicit tools allowlist; "*" enables all.
            server["tools"] = new JsonArray { "*" };
        }

        return server;
    }

    private static JsonObject BuildRawServer(JsonObject raw, string assetId, ProviderName provider, InstallScope scope)
    {
        var transportText = StringValue(raw, "transport") ?? StringValue(raw, "type") ?? "stdio";
        var transport = EnumParsers.ParseTransport(transportText, $"MCP asset '{assetId}'");
        var server = new JsonObject { ["type"] = TransportName(transport) };
        if (transport == McpTransport.Stdio)
        {
            server["command"] = StringValue(raw, "command") ?? "";
            server["args"] = StringArray(raw["args"]);
            if (!OmitEnvObject(provider, scope))
            {
                server["env"] = EnvObjectFromRaw(raw, assetId, provider, scope);
            }

            var cwd = StringValue(raw, "cwd");
            if (!string.IsNullOrWhiteSpace(cwd)) server["cwd"] = cwd;
        }
        else
        {
            server["url"] = StringValue(raw, "url") ?? "";
            if (raw["headerEnvVars"] is JsonObject headers)
            {
                var normalized = new JsonObject();
                foreach (var (header, value) in headers)
                {
                    normalized[header] = EnvPlaceholder(provider, scope, value?.GetValue<string>() ?? "");
                }

                server["headers"] = normalized;
            }
        }

        var tools = StringArray(raw["tools"]);
        if (tools.Count > 0) server["tools"] = tools;
        return server;
    }

    private static void MergeJson(string path, string rootKey, JsonObject incomingServers,
        string? previousFragment, bool overwriteModified, Action<string> backupIfExists)
    {
        var root = UserConfigJson.LoadObject(path);
        var previousServers = previousFragment is null ? null : UserConfigJson.ParseFragment(previousFragment);

        if (root[rootKey] is not JsonObject existingServers)
        {
            existingServers = new JsonObject();
            root[rootKey] = existingServers;
        }

        var changed = false;
        foreach (var (serverId, incoming) in incomingServers)
        {
            if (incoming is null) continue;
            if (existingServers[serverId] is JsonNode existing)
            {
                if (JsonNode.DeepEquals(existing, incoming)) continue;

                // Our previous install (or a drift decision to overwrite) may be replaced;
                // anything else in the user's file is theirs and stays a conflict.
                var isOurs = previousServers?[serverId] is JsonNode previous && JsonNode.DeepEquals(existing, previous);
                if (!isOurs && !overwriteModified)
                {
                    throw new AgentPackException(
                        $"MCP server '{serverId}' already exists in {path} with a different configuration.",
                        "Remove or rename the existing entry, then rerun the install.",
                        ExitCodes.DriftOrConflict);
                }
            }

            existingServers[serverId] = Clone(incoming);
            changed = true;
        }

        if (!changed && File.Exists(path)) return;
        BackupIfExists(path, backupIfExists);
        AtomicWrite.Text(path, root.ToJsonString(JsonOptions) + Environment.NewLine);
    }

    private static void MergeCodexToml(string path, JsonObject incomingServers,
        string? previousFragment, bool overwriteModified, Action<string> backupIfExists)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var changed = false;

        foreach (var (serverId, serverNode) in incomingServers)
        {
            if (serverNode is not JsonObject server) continue;
            var block = BuildCodexTomlBlock(serverId, server);
            var header = TomlHeader(serverId);
            var existing = FindTomlSection(text, header);
            if (existing is not null)
            {
                if (NormalizeToml(existing).Equals(NormalizeToml(block), StringComparison.Ordinal)) continue;

                var previous = previousFragment is null ? null : FindTomlSection(previousFragment, header);
                var isOurs = previous is not null && NormalizeToml(existing).Equals(NormalizeToml(previous), StringComparison.Ordinal);
                if (!isOurs && !overwriteModified)
                {
                    throw new AgentPackException(
                        $"MCP server '{serverId}' already exists in {path} with a different configuration.",
                        "Remove or rename the existing [mcp_servers] section, then rerun the install.",
                        ExitCodes.DriftOrConflict);
                }

                text = RemoveTomlSection(text, header);
            }

            var builder = new StringBuilder(text.TrimEnd());
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append(block.TrimEnd());
            text = builder.ToString();
            changed = true;
        }

        if (!changed && File.Exists(path)) return;
        BackupIfExists(path, backupIfExists);
        AtomicWrite.Text(path, text.TrimEnd() + Environment.NewLine);
    }

    private static FragmentState CheckToml(string path, string fragment)
    {
        var text = File.ReadAllText(path);
        var states = new List<FragmentState>();
        foreach (var (header, block) in EnumerateTomlBlocks(fragment))
        {
            var existing = FindTomlSection(text, header);
            states.Add(existing is null
                ? FragmentState.Absent
                : NormalizeToml(existing).Equals(NormalizeToml(block), StringComparison.Ordinal) ? FragmentState.Present : FragmentState.Modified);
        }

        if (states.Count == 0 || states.All(x => x == FragmentState.Absent)) return FragmentState.Absent;
        return states.All(x => x == FragmentState.Present) ? FragmentState.Present : FragmentState.Modified;
    }

    private static void RemoveToml(string path, string fragment, Action<string> backupIfExists)
    {
        var text = File.ReadAllText(path);
        var changed = false;
        foreach (var (header, _) in EnumerateTomlBlocks(fragment))
        {
            if (FindTomlSection(text, header) is null) continue;
            text = RemoveTomlSection(text, header);
            changed = true;
        }

        if (!changed) return;
        backupIfExists(path);
        AtomicWrite.Text(path, text.TrimEnd() + Environment.NewLine);
    }

    private static string TomlFragment(JsonObject servers)
    {
        var builder = new StringBuilder();
        foreach (var (serverId, serverNode) in servers)
        {
            if (serverNode is not JsonObject server) continue;
            if (builder.Length > 0) builder.AppendLine();
            builder.AppendLine(BuildCodexTomlBlock(serverId, server).TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Splits a stored TOML fragment back into its per-server sections.</summary>
    private static IEnumerable<(string Header, string Block)> EnumerateTomlBlocks(string fragment)
    {
        var lines = fragment.Replace("\r\n", "\n").Split('\n');
        var headers = lines.Select(x => x.Trim()).Where(x => x.StartsWith("[mcp_servers.", StringComparison.Ordinal)).ToList();
        foreach (var header in headers)
        {
            var block = FindTomlSection(fragment, header);
            if (block is not null) yield return (header, block);
        }
    }

    private static string RemoveTomlSection(string text, string header)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var startLine = lines.FindIndex(x => x.Trim().Equals(header, StringComparison.Ordinal));
        if (startLine < 0) return text;

        var endLine = lines.Count;
        for (var i = startLine + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                endLine = i;
                break;
            }
        }

        lines.RemoveRange(startLine, endLine - startLine);
        return string.Join(newline, lines).Trim() is { Length: > 0 } remaining ? remaining + newline : "";
    }

    private static string BuildCodexTomlBlock(string serverId, JsonObject server)
    {
        var lines = new List<string> { TomlHeader(serverId) };
        var type = StringValue(server, "type") ?? "stdio";
        if (!type.Equals("stdio", StringComparison.OrdinalIgnoreCase)) lines.Add("type = " + TomlString(type));
        AddTomlString(lines, "command", StringValue(server, "command"));
        AddTomlString(lines, "url", StringValue(server, "url"));
        AddTomlArray(lines, "args", StringArray(server["args"]));
        if (server["env"] is JsonObject env)
        {
            AddTomlArray(lines, "env_vars", env.Select(x => x.Key).Order(StringComparer.OrdinalIgnoreCase));
        }

        AddTomlString(lines, "cwd", StringValue(server, "cwd"));
        AddTomlArray(lines, "enabled_tools", StringArray(server["tools"]));
        if (server["headers"] is JsonObject headers && headers.Count > 0)
        {
            // Codex maps headers to environment variables via env_http_headers,
            // so the value is the env var NAME, not a placeholder.
            var headerPairs = headers
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => TomlKey(x.Key) + " = " + TomlString(EnvVarName(x.Value?.GetValue<string>() ?? "")))
                .ToList();
            lines.Add("env_http_headers = { " + string.Join(", ", headerPairs) + " }");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Strips a ${VAR} or ${env:VAR} placeholder down to the bare env var name.</summary>
    private static string EnvVarName(string placeholder)
    {
        var name = placeholder.Trim();
        if (name.StartsWith("${", StringComparison.Ordinal) && name.EndsWith('}')) name = name[2..^1];
        if (name.StartsWith("env:", StringComparison.Ordinal)) name = name[4..];
        return name;
    }

    private static string TransportName(McpTransport transport) => transport.ToString().ToLowerInvariant();

    private static string? FindMcpJson(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        if (File.Exists(sourcePath)) return sourcePath;
        if (!Directory.Exists(sourcePath)) return null;
        var preferred = Path.Combine(sourcePath, "mcp.json");
        if (File.Exists(preferred)) return preferred;
        return Directory.EnumerateFiles(sourcePath, "*.json", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static JsonObject EnvObject(IEnumerable<string> envVars, ProviderName provider, InstallScope scope)
    {
        var env = new JsonObject();
        foreach (var variable in envVars.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            env[variable] = EnvPlaceholder(provider, scope, variable);
        }

        return env;
    }

    private static JsonObject EnvObjectFromRaw(JsonObject raw, string assetId, ProviderName provider, InstallScope scope)
    {
        if (raw["envVars"] is JsonArray envVars)
        {
            return EnvObject(envVars.Select(x => x?.GetValue<string>() ?? ""), provider, scope);
        }

        var env = new JsonObject();
        if (raw["env"] is not JsonObject rawEnv) return env;

        foreach (var (key, value) in rawEnv)
        {
            var stringValue = value?.GetValue<string>() ?? "";
            if (!stringValue.Equals("${" + key + "}", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(stringValue))
            {
                throw new AgentPackException(
                    $"MCP asset '{assetId}' env var '{key}' must be declared by name, not stored with a value.",
                    "Secrets never live in the catalog. Users set the variable in their environment.");
            }

            env[key] = EnvPlaceholder(provider, scope, key);
        }

        return env;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonArray StringArray(JsonNode? node)
    {
        var array = new JsonArray();
        if (node is not JsonArray values) return array;
        foreach (var value in values)
        {
            if (value is not null) array.Add(value.GetValue<string>());
        }

        return array;
    }

    private static string? StringValue(JsonObject obj, string key) => obj[key]?.GetValue<string>();
    private static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    private static void AddTomlString(List<string> lines, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{key} = {TomlString(value)}");
    }

    private static void AddTomlArray(List<string> lines, string key, JsonArray values) =>
        AddTomlArray(lines, key, values.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));

    private static void AddTomlArray(List<string> lines, string key, IEnumerable<string> values)
    {
        var strings = values.ToList();
        if (strings.Count == 0) return;
        lines.Add($"{key} = [{string.Join(", ", strings.Select(TomlString))}]");
    }

    private static string TomlHeader(string serverId) => "[mcp_servers." + TomlKey(serverId) + "]";
    private static string TomlKey(string value) => BareTomlKey.IsMatch(value) ? value : TomlString(value);
    private static string TomlString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? FindTomlSection(string text, string header)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var startLine = Array.FindIndex(lines, x => x.Trim().Equals(header, StringComparison.Ordinal));
        if (startLine < 0) return null;
        var endLine = lines.Length;
        for (var i = startLine + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith('['))
            {
                endLine = i;
                break;
            }
        }

        return string.Join('\n', lines.Skip(startLine).Take(endLine - startLine)).Trim();
    }

    private static string NormalizeToml(string text) => text.Replace("\r\n", "\n").Trim();

    private static void BackupIfExists(string path, Action<string> backupIfExists)
    {
        if (File.Exists(path) || Directory.Exists(path)) backupIfExists(path);
    }
}

public static class AtomicWrite
{
    // BOM-less UTF-8: strict JSON parsers reject a leading BOM.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Text(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempPath, contents, Utf8NoBom);
        File.Move(tempPath, fullPath, overwrite: true);
    }
}
