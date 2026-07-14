namespace AgentPack.Core;

/// <summary>
/// One source of truth for projecting portable agent capabilities into native
/// provider tool names and for explaining where enforcement is only coarse.
/// </summary>
public static class AgentCompatibility
{
    public static AgentProviderProjection Project(
        Asset agent,
        ProviderName provider,
        ResolvedAgentDependencies dependencies)
    {
        var configuredTools = agent.Agent?.Tools;
        const string model = "current/default";

        if (configuredTools is null)
        {
            return new AgentProviderProjection(
                provider,
                "inherit all native tools",
                "native inheritance",
                model,
                "No portable tool restriction was declared.");
        }

        if (provider is ProviderName.Claude or ProviderName.Copilot)
        {
            var native = NativeTools(agent, provider, dependencies) ?? [];
            var note = provider == ProviderName.Copilot && configuredTools.Contains(AgentTool.Web)
                ? "Exact allowlist; GitHub cloud currently ignores the web alias."
                : "Exact native allowlist.";
            return new AgentProviderProjection(provider, string.Join(", ", native), "exact", model, note);
        }

        var readOnly = !configuredTools.Contains(AgentTool.Edit) && !configuredTools.Contains(AgentTool.Execute);
        return provider switch
        {
            ProviderName.Codex => new AgentProviderProjection(
                provider,
                readOnly ? "parent tools; read-only sandbox" : "parent tools; writable sandbox",
                "coarse",
                model,
                readOnly
                    ? "Codex has no granular per-agent allowlist; prompt policy plus read-only sandbox."
                    : "Codex has no granular per-agent allowlist; execute/edit requires the writable parent surface."),
            ProviderName.Cursor => new AgentProviderProjection(
                provider,
                readOnly ? "parent tools; readonly: true" : "parent tools; readonly: false",
                "coarse",
                model,
                readOnly
                    ? "Cursor has no granular tools field; prompt policy plus readonly mode."
                    : "Cursor has no granular tools field; execute/edit requires the writable parent surface."),
            _ => throw new AgentPackException($"Agent provider '{provider}' is unsupported.")
        };
    }

    public static IReadOnlyList<string>? NativeTools(
        Asset agent,
        ProviderName provider,
        ResolvedAgentDependencies dependencies)
    {
        if (agent.Agent?.Tools is null) return null;
        if (provider is not (ProviderName.Claude or ProviderName.Copilot)) return null;
        var result = new List<string>();
        foreach (var tool in agent.Agent.Tools)
        {
            result.AddRange(provider switch
            {
                ProviderName.Claude => tool switch
                {
                    AgentTool.Read => ["Read"],
                    AgentTool.Search => ["Glob", "Grep"],
                    AgentTool.Edit => ["Edit", "Write"],
                    AgentTool.Execute => ["Bash"],
                    AgentTool.Web => ["WebFetch", "WebSearch"],
                    AgentTool.Agent => ["Agent"],
                    _ => []
                },
                ProviderName.Copilot => [tool.ToString().ToLowerInvariant()],
                _ => []
            });
        }

        if (provider == ProviderName.Claude)
        {
            result.AddRange(dependencies.Mcp.SelectMany(x =>
                x.Mcp!.Tools.Select(tool => $"mcp__{x.Mcp.Server}__{tool}")));
        }
        else
        {
            result.AddRange(dependencies.Mcp.SelectMany(x =>
                x.Mcp!.Tools.Select(tool => $"{x.Mcp.Server}/{tool}")));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed record AgentProviderProjection(
    ProviderName Provider,
    string NativeTools,
    string Enforcement,
    string Model,
    string Note);
