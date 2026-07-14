using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class AgentExplainCommand : Command<AgentExplainCommand.Settings>
{
    public sealed class Settings : ProviderScopeSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Agent id. Omit in an interactive terminal to pick from the catalog.")]
        public string? Id { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var agents = loaded.Catalog.Assets
            .Where(x => x.Kind == AssetKind.Agents)
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        if (agents.Count == 0)
            throw new AgentPackException("The catalog contains no agents.");

        Asset agent;
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            if (!Output.CanPrompt)
                throw new AgentPackException(
                    "No agent id specified and no interactive terminal is available.",
                    "Run 'agentpack agent explain <id>' or 'agentpack list agents'.");
            agent = Prompts.SelectAgent(agents, "Select an agent to explain");
        }
        else
        {
            agent = agents.FirstOrDefault(x => x.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase))
                ?? throw new AgentPackException(
                    $"Unknown agent '{settings.Id}'." +
                    (Suggestions.Nearest(settings.Id, agents.Select(x => x.Id)) is { } near
                        ? $" Did you mean '{near}'?"
                        : ""),
                    "Run 'agentpack list agents' to see available agents.");
        }

        var providers = settings.ExplicitProviders();
        if (providers.Count == 0) providers = agent.Providers;
        var unsupported = providers.Where(x => !agent.Providers.Contains(x)).ToList();
        if (unsupported.Count > 0)
        {
            throw new AgentPackException(
                $"Agent '{agent.Id}' does not target: {string.Join(", ", unsupported.Select(x => x.Display()))}.",
                "Change the agent's providers list or choose one of its supported providers.");
        }

        Output.Info($"{agent.Name} ({agent.Id}) — {agent.Description}");
        if (agent.Source is AssetSource.External external)
            Output.Info($"Pinned external source: {external.Url}@{external.Ref}");
        Output.AgentCompatibility(loaded, providers.Select(x => (agent, x)));

        var tools = agent.Agent?.Tools is null
            ? "inherit"
            : string.Join(", ", agent.Agent.Tools.Select(x => x.ToString().ToLowerInvariant()));
        Output.Info($"Portable manifest tools: {tools}.");
        Output.Info("Model: current/default. AgentPack always strips model metadata and uses the model selected by the user, session, or workflow.");
        return ExitCodes.Ok;
    }
}
