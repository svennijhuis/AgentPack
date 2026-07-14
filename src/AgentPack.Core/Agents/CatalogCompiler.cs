namespace AgentPack.Core;

using Tomlyn.Parsing;

public sealed class CatalogCompiler
{
    private readonly AgentRenderer _renderer;
    private readonly AssetResolver _resolver;

    public CatalogCompiler(AgentPackPaths paths)
    {
        _renderer = new AgentRenderer(paths);
        _resolver = new AssetResolver(paths);
    }

    public CompileResult Compile(LoadedCatalog loaded)
    {
        var outputs = new List<CompiledAgent>();
        var warnings = new List<CompileWarning>();
        foreach (var agent in loaded.Catalog.Assets.Where(x => x.Kind == AssetKind.Agents))
        {
            var inspection = ExternalAgentFrontmatter.Inspect(_resolver.Resolve(loaded, agent));
            if (inspection?.Model is { } model)
            {
                warnings.Add(new CompileWarning(
                    "agent.model.stripped",
                    agent.Id,
                    $"Agent '{agent.Id}' source declares model '{model}'. Compilation removed it; generated agents use the user's, session's, or workflow's current/default model."));
            }

            foreach (var provider in agent.Providers)
            {
                foreach (var scope in Enum.GetValues<InstallScope>())
                {
                    try
                    {
                        var content = _renderer.Render(loaded, agent, provider, scope);
                        ValidateSyntax(content, agent, provider);
                        outputs.Add(new CompiledAgent(agent.Id, provider, scope, ContentHash.ComputeText(content)));
                    }
                    catch (AgentPackException) { throw; }
                    catch (Exception ex)
                    {
                        throw new AgentPackException(
                            $"[agent.compile.syntax] Agent '{agent.Id}' failed to compile for {provider.Display()} ({scope.ToString().ToLowerInvariant()}): {ex.Message}",
                            "Correct the agent manifest or imported content and rerun 'agentpack catalog compile'.",
                            ExitCodes.ValidationFailed);
                    }
                }
            }
        }
        return new CompileResult(outputs, warnings);
    }

    public static void ValidateSyntax(string content, Asset agent, ProviderName provider)
    {
        try
        {
            ValidateSyntaxCore(content, provider);
        }
        catch (Exception ex) when (ex is not AgentPackException)
        {
            throw new AgentPackException(
                $"[agent.compile.syntax] Agent '{agent.Id}' generated invalid {provider.Display()} syntax: {ex.Message}",
                "Correct the agent manifest or imported content and rerun 'agentpack catalog compile'.",
                ExitCodes.ValidationFailed);
        }
    }

    private static void ValidateSyntaxCore(string content, ProviderName provider)
    {
        if (provider != ProviderName.Codex)
        {
            var normalized = content.Replace("\r\n", "\n");
            var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
                throw new FormatException("generated Markdown frontmatter is incomplete");
            _ = CatalogLoader.FromYaml<Dictionary<string, object?>>(normalized[4..end]);
            return;
        }

        SyntaxParser.ParseStrict(content, "generated-agent.toml", validate: true);
        if (!content.Contains("name = \"", StringComparison.Ordinal) ||
            !content.Contains("description = \"", StringComparison.Ordinal) ||
            !content.Contains("developer_instructions = \"", StringComparison.Ordinal))
            throw new FormatException("generated TOML is missing a required agent key");
    }
}

public sealed record CompiledAgent(string Id, ProviderName Provider, InstallScope Scope, string Checksum);
public sealed record CompileWarning(string Code, string AgentId, string Message);
public sealed record CompileResult(
    IReadOnlyList<CompiledAgent> Outputs,
    IReadOnlyList<CompileWarning> Warnings);
