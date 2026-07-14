using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPack.Core;

/// <summary>Compiles a provider-neutral agent and its private imports to a native provider file.</summary>
public sealed class AgentRenderer
{
    private readonly AssetResolver _resolver;

    public AgentRenderer(AgentPackPaths paths) => _resolver = new AssetResolver(paths);

    public string Render(LoadedCatalog loaded, Asset agent, ProviderName provider, InstallScope scope)
    {
        EnsureDescription(agent);
        var dependencies = new AgentDependencyResolver(loaded.Catalog).Resolve(agent, provider);
        var body = ComposeBody(loaded, agent, dependencies);
        return provider switch
        {
            ProviderName.Claude => RenderMarkdown(agent, provider, scope, dependencies, body),
            ProviderName.Copilot => RenderMarkdown(agent, provider, scope, dependencies, body),
            ProviderName.Cursor => RenderMarkdown(agent, provider, scope, dependencies, body),
            ProviderName.Codex => RenderCodex(agent, scope, dependencies, body),
            _ => throw new AgentPackException($"Agent provider '{provider}' is unsupported.")
        };
    }

    public string Fingerprint(LoadedCatalog loaded, Asset agent, ProviderName provider, InstallScope scope)
    {
        EnsureDescription(agent);
        var dependencies = new AgentDependencyResolver(loaded.Catalog).Resolve(agent, provider);
        var parts = new List<string?>
        {
            "agent-render-v1", agent.Id, agent.Version.ToString(), EffectiveContentChecksum(loaded, agent),
            provider.Display(), scope.ToString(), agent.Description,
            agent.Agent?.Tools is null ? "tools:inherit" : "tools:" + string.Join(',', agent.Agent.Tools)
        };
        foreach (var dependency in dependencies.All.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add($"{dependency.Kind}:{dependency.Id}:{dependency.Version}:{EffectiveContentChecksum(loaded, dependency)}");
            if (dependency.Mcp is not null) parts.Add(JsonSerializer.Serialize(dependency.Mcp));
        }

        return ContentHash.ComputeText(string.Join("\u001f", parts.Select(x => x ?? "")));
    }

    private string? EffectiveContentChecksum(LoadedCatalog loaded, Asset asset)
    {
        var declared = loaded.EffectiveChecksum(asset);
        if (declared is not null) return declared;
        var path = _resolver.TryResolve(loaded, asset);
        return path is null ? null : ContentHash.Compute(path);
    }

    private string ComposeBody(LoadedCatalog loaded, Asset agent, ResolvedAgentDependencies dependencies)
    {
        var source = _resolver.Resolve(loaded, agent);
        var builder = new StringBuilder(StripFrontmatter(ReadMarkdown(source, agent.Id, "agent"))).AppendLine();

        foreach (var instruction in dependencies.Instructions)
        {
            var content = StripFrontmatter(ReadMarkdown(_resolver.Resolve(loaded, instruction), instruction.Id, "instruction"));
            builder.AppendLine().AppendLine($"<!-- agentpack:instruction:{instruction.Id}@{instruction.Version} -->");
            builder.AppendLine(content.Trim());
            builder.AppendLine($"<!-- /agentpack:instruction:{instruction.Id} -->");
        }

        if (agent.Agent?.Tools is { } portableTools)
        {
            builder.AppendLine().AppendLine("## AgentPack capability policy");
            builder.AppendLine("Use only these declared built-in capability classes: " +
                string.Join(", ", portableTools.Select(x => $"`{x.ToString().ToLowerInvariant()}`")) + ".");
            builder.AppendLine("Do not use other provider built-ins. Provider sandbox and approval policy still apply.");
        }

        if (dependencies.Skills.Count > 0)
        {
            builder.AppendLine().AppendLine("## Required skills");
            builder.AppendLine("Use these installed AgentPack skills when their instructions apply: " +
                string.Join(", ", dependencies.Skills.Select(x => $"`{x.Id}`")) + ".");
        }

        if (dependencies.Mcp.Count > 0)
        {
            builder.AppendLine().AppendLine("## Required MCP tools");
            foreach (var mcp in dependencies.Mcp)
            {
                builder.AppendLine($"- `{mcp.Id}`: {string.Join(", ", mcp.Mcp!.Tools.Select(x => $"`{x}`"))}");
            }
        }

        return builder.ToString().Trim() + Environment.NewLine;
    }

    private string RenderMarkdown(
        Asset agent,
        ProviderName provider,
        InstallScope scope,
        ResolvedAgentDependencies dependencies,
        string body)
    {
        var lines = new List<string>
        {
            "---",
            "name: " + YamlString(agent.Id),
            "description: " + YamlString(agent.Description)
        };

        var tools = AgentCompatibility.NativeTools(agent, provider, dependencies);
        if (tools is not null) lines.Add("tools: [" + string.Join(", ", tools.Select(YamlString)) + "]");
        if (provider == ProviderName.Claude && dependencies.Skills.Count > 0)
        {
            lines.Add("skills: [" + string.Join(", ", dependencies.Skills.Select(x => YamlString(x.Id))) + "]");
        }

        if (provider == ProviderName.Cursor)
        {
            if (agent.Agent?.Tools is not null)
            {
                var readOnly = !agent.Agent.Tools.Contains(AgentTool.Edit) && !agent.Agent.Tools.Contains(AgentTool.Execute);
                lines.Add("readonly: " + readOnly.ToString().ToLowerInvariant());
            }
        }

        // Agent-local MCP is rendered separately below; inline JSON is valid YAML.
        if (provider is ProviderName.Claude or ProviderName.Copilot && dependencies.Mcp.Count > 0)
        {
            var servers = new JsonObject();
            foreach (var dependency in dependencies.Mcp)
            {
                var built = McpMerger.BuildServers(dependency, null, provider, scope, agentLocal: true);
                foreach (var (key, value) in built) servers[key] = value?.DeepClone();
            }
            if (provider == ProviderName.Claude)
            {
                // Claude's agent schema uses a list whose entries are either configured
                // server names or one-entry inline server maps.
                var inlineServers = new JsonArray();
                foreach (var (key, value) in servers)
                    inlineServers.Add(new JsonObject { [key] = value?.DeepClone() });
                lines.Add("mcpServers: " + inlineServers.ToJsonString());
            }
            else
            {
                lines.Add("mcp-servers: " + servers.ToJsonString());
            }
        }

        lines.Add("---");
        lines.Add("");
        lines.Add(body.TrimEnd());
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private string RenderCodex(
        Asset agent,
        InstallScope scope,
        ResolvedAgentDependencies dependencies,
        string body)
    {
        var lines = new List<string>
        {
            "name = " + TomlString(agent.Id),
            "description = " + TomlString(agent.Description),
            "developer_instructions = " + TomlString(body)
        };
        if (agent.Agent?.Tools is not null &&
            !agent.Agent.Tools.Contains(AgentTool.Edit) &&
            !agent.Agent.Tools.Contains(AgentTool.Execute))
        {
            lines.Add("sandbox_mode = \"read-only\"");
        }

        foreach (var dependency in dependencies.Mcp)
        {
            var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, "", InstallMode.MergeMcp, true);
            lines.Add("");
            lines.Add(McpMerger.Preview(dependency, null, target, scope));
        }
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    private static void EnsureDescription(Asset agent)
    {
        if (string.IsNullOrWhiteSpace(agent.Description))
        {
            throw new AgentPackException(
                $"[agent.description.missing] Agent '{agent.Id}' requires a non-empty top-level description.",
                $"Add 'description:' to assets/agents/{agent.Id}/agentpack.yaml before compiling or installing.",
                ExitCodes.ValidationFailed);
        }
    }

    private static string ReadMarkdown(string path, string id, string role)
    {
        if (File.Exists(path)) return File.ReadAllText(path);
        if (!Directory.Exists(path)) throw new AgentPackException($"The {role} source for '{id}' does not exist: {path}");

        var markdown = Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var preferredNames = new[] { "AGENT.md", id + ".agent.md", id + ".md", "SKILL.md" };
        foreach (var name in preferredNames)
        {
            var match = markdown.FirstOrDefault(x => Path.GetFileName(x).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return File.ReadAllText(match);
        }
        if (markdown.Count == 1) return File.ReadAllText(markdown[0]);
        throw new AgentPackException(
            $"The {role} source for '{id}' has {markdown.Count} Markdown candidates.",
            $"Add one unambiguous AGENT.md or {id}.md file.", ExitCodes.ValidationFailed);
    }

    public static string StripFrontmatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return normalized.Trim();
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? normalized.Trim() : normalized[(end + 5)..].Trim();
    }

    private static string YamlString(string value) => JsonSerializer.Serialize(value);
    private static string TomlString(string value) => JsonSerializer.Serialize(value);

}
