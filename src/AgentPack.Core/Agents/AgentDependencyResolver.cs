namespace AgentPack.Core;

/// <summary>Resolves the manifest's typed agent imports against the one effective catalog.</summary>
public sealed class AgentDependencyResolver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Asset>> _assets;

    public AgentDependencyResolver(Catalog catalog)
    {
        _assets = catalog.Assets
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (IReadOnlyList<Asset>)x.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public ResolvedAgentDependencies Resolve(Asset agent, ProviderName provider)
    {
        if (agent.Kind != AssetKind.Agents || agent.Agent is null)
        {
            throw new AgentPackException($"Asset '{agent.Id}' is not a valid agent.", exitCode: ExitCodes.ValidationFailed);
        }

        var instructions = Resolve(agent, agent.Agent.Imports.Instructions, AssetKind.Instructions, provider);
        var skills = Resolve(agent, agent.Agent.Imports.Skills, AssetKind.Skills, provider);
        var mcp = Resolve(agent, agent.Agent.Imports.Mcp, AssetKind.Mcp, provider);
        return new ResolvedAgentDependencies(instructions, skills, mcp);
    }

    private IReadOnlyList<Asset> Resolve(
        Asset agent,
        IReadOnlyList<AgentAssetReference> imports,
        AssetKind expectedKind,
        ProviderName provider)
    {
        var result = new List<Asset>();
        foreach (var import in imports)
        {
            if (!_assets.TryGetValue(import.Id, out var matches) || matches.Count == 0)
            {
                throw DependencyError("missing", agent, import, provider,
                    $"No catalog asset named '{import.Id}' exists.", "Add the dependency or correct its id.");
            }

            if (matches.Count != 1)
            {
                throw DependencyError("ambiguous", agent, import, provider,
                    $"The effective catalog contains {matches.Count} assets named '{import.Id}'.",
                    "Remove the duplicate or use a project overlay to select one effective asset.");
            }

            var asset = matches[0];
            if (asset.Kind != expectedKind)
            {
                throw DependencyError("kind", agent, import, provider,
                    $"'{import.Id}' is {asset.Kind.Display()}, but this import requires {expectedKind.Display()}.",
                    "Move the reference to the correct typed import list.");
            }

            if (import.VersionRange is not null && !import.VersionRange.Contains(asset.Version))
            {
                throw DependencyError("version", agent, import, provider,
                    $"Agent '{agent.Id}' requires {expectedKind.Display().TrimEnd('s')} '{import.Id}' at {import.VersionRange}, but the effective catalog contains {asset.Version}.",
                    "Update the agent's compatibility range or restore a compatible catalog version before compiling.");
            }

            if (!asset.Providers.Contains(provider))
            {
                throw DependencyError("provider", agent, import, provider,
                    $"Dependency '{import.Id}' does not support provider '{provider.Display()}'.",
                    "Add provider support to the dependency or remove that provider from the agent.");
            }

            if (asset.Status is AssetStatus.Blocked or AssetStatus.Deprecated)
            {
                throw DependencyError("status", agent, import, provider,
                    $"Dependency '{import.Id}' is {asset.Status.ToString().ToLowerInvariant()}.",
                    "Replace it with an active catalog asset before compiling.");
            }

            result.Add(asset);
        }

        return result;
    }

    private static AgentPackException DependencyError(
        string suffix,
        Asset agent,
        AgentAssetReference dependency,
        ProviderName provider,
        string message,
        string action) => new(
        $"[agent.dependency.{suffix}] {message}\nAgent: {agent.Id}\nDependency: {dependency.Id}\nProvider: {provider.Display()}",
        action,
        ExitCodes.ValidationFailed);
}

public sealed record ResolvedAgentDependencies(
    IReadOnlyList<Asset> Instructions,
    IReadOnlyList<Asset> Skills,
    IReadOnlyList<Asset> Mcp)
{
    public IEnumerable<Asset> All => Instructions.Concat(Skills).Concat(Mcp);
}
