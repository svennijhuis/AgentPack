using System.Text.RegularExpressions;
using AgentPack.Core.Primitives;

namespace AgentPack.Core.Serialization;

/// <summary>
/// Converts YAML DTOs into the immutable typed model. All string-to-type parsing
/// happens here; errors are accumulated per entity so a broken catalog reports
/// everything wrong at once instead of failing on the first field.
/// </summary>
public static class CatalogMapper
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static Catalog Map(CatalogDto dto, List<CatalogIssue> issues)
    {
        var groups = dto.Groups.Select(g => MapGroup(g, issues)).Where(g => g is not null).Select(g => g!).ToList();
        var assets = dto.Assets.Select(a => MapAsset(a, issues)).Where(a => a is not null).Select(a => a!).ToList();
        var profiles = dto.Profiles.Select(p => MapProfile(p, dto.Bundles, issues)).Where(p => p is not null).Select(p => p!).ToList();

        if (dto.Bundles.Count > 0)
        {
            issues.Add(new CatalogIssue(IssueSeverity.Warning, "catalog.bundles.removed",
                $"Bundles are no longer supported ({string.Join(", ", dto.Bundles.Select(b => b.Id))}). " +
                "Their assets were folded into the profiles that referenced them. Move the asset lists into profiles and delete the bundles."));
        }

        return new Catalog
        {
            SchemaVersion = dto.SchemaVersion,
            CatalogVersion = dto.CatalogVersion,
            Groups = groups,
            Assets = assets,
            Profiles = profiles
        };
    }

    public static Asset? MapAsset(AssetDto dto, List<CatalogIssue> issues)
    {
        var context = $"asset '{(string.IsNullOrWhiteSpace(dto.Id) ? "<missing id>" : dto.Id)}'";
        var before = issues.Count(x => x.Severity == IssueSeverity.Error);

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            Error(issues, "asset.id.missing", $"{context}: id is missing (normally inferred from the asset folder name).");
        }
        else if (!IdPattern.IsMatch(dto.Id))
        {
            Error(issues, "asset.id.invalid", $"{context}: id must be kebab-case (lowercase letters, digits, dashes).");
        }

        if (!AssetKinds.TryParse(dto.Kind, out var kind))
        {
            Error(issues, "asset.kind.invalid",
                $"{context}: unknown kind '{dto.Kind}'. Valid kinds: {string.Join(", ", AssetKinds.All.Select(AssetKinds.Display))}.");
        }

        if (!SemVersion.TryParse(string.IsNullOrWhiteSpace(dto.Version) ? "1.0.0" : dto.Version, out var version))
        {
            Error(issues, "asset.version.invalid", $"{context}: version '{dto.Version}' is not valid semver (expected MAJOR.MINOR.PATCH).");
        }

        var status = TryEnum(() => EnumParsers.ParseStatus(dto.Status, context), AssetStatus.Recommended, issues);
        var channel = TryEnum(() => EnumParsers.ParseChannel(dto.Channel, context), Channel.Stable, issues);
        var providers = MapProviders(dto.Providers, context, issues);
        var source = MapSource(dto.Source, context, issues);
        var mcp = MapMcp(dto, context, issues);
        var hook = MapHook(dto, context, issues);
        var agent = MapAgent(dto, context, issues);

        if (issues.Count(x => x.Severity == IssueSeverity.Error) > before) return null;

        return new Asset
        {
            Id = dto.Id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name,
            Kind = kind,
            Version = version,
            Description = dto.Description,
            Groups = dto.Groups,
            Tags = dto.Tags,
            Providers = providers,
            Owner = string.IsNullOrWhiteSpace(dto.Owner) ? null : dto.Owner,
            Status = status,
            Channel = channel,
            Source = source!,
            Mcp = mcp,
            Hook = hook,
            Agent = agent
        };
    }

    private static AgentSpec? MapAgent(AssetDto dto, string context, List<CatalogIssue> issues)
    {
        if (dto.Agent is null) return null;
        var imports = dto.Agent.Imports ?? new AgentImportsDto();
        var unsupportedImports = new (string Name, IReadOnlyList<AgentImportDto> Values)[]
        {
            ("agents", imports.Agents), ("hooks", imports.Hooks),
            ("rules", imports.Rules), ("prompts", imports.Prompts),
            ("tools", imports.Tools), ("templates", imports.Templates)
        };
        foreach (var (name, values) in unsupportedImports.Where(x => x.Values.Count > 0))
        {
            Error(issues, "agent.import.unsupported",
                $"{context}: agent.imports.{name} is not supported in schema v1. Only instructions, skills, and mcp can be imported.");
        }
        if (dto.Agent.Tools is { Count: 0 })
        {
            Error(issues, "agent.tools.empty", $"{context}: agent.tools cannot be empty; omit it to inherit provider tools.");
        }

        List<AgentTool>? tools = null;
        if (dto.Agent.Tools is { Count: > 0 })
        {
            tools = [];
            foreach (var value in dto.Agent.Tools.SelectMany(SplitList))
            {
                try
                {
                    var tool = EnumParsers.ParseAgentTool(value, context);
                    if (!tools.Contains(tool)) tools.Add(tool);
                    else Error(issues, "agent.tools.duplicate", $"{context}: agent tool '{value}' is listed more than once.");
                }
                catch (AgentPackException ex)
                {
                    Error(issues, "agent.tool.invalid", ex.Message);
                }
            }
        }

        if (dto.Agent.Models is { Count: > 0 })
        {
            Warning(issues, "agent.model.ignored",
                $"{context}: agent.models is ignored and never rendered. AgentPack always uses the model selected by the user, session, or workflow; remove the models mapping when convenient.");
        }

        return new AgentSpec
        {
            Tools = tools,
            Imports = new AgentImports
            {
                Instructions = MapAgentImports(imports.Instructions, context, "instructions", issues),
                Skills = MapAgentImports(imports.Skills, context, "skills", issues),
                Mcp = MapAgentImports(imports.Mcp, context, "mcp", issues)
            }
        };
    }

    private static IReadOnlyList<AgentAssetReference> MapAgentImports(
        IReadOnlyList<AgentImportDto> values, string context, string field, List<CatalogIssue> issues)
    {
        var result = new List<AgentAssetReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value.Id) || !IdPattern.IsMatch(value.Id))
            {
                Error(issues, "agent.import.id.invalid", $"{context}: agent.imports.{field} contains invalid id '{value.Id}'.");
                continue;
            }

            if (!seen.Add(value.Id))
            {
                Error(issues, "agent.import.duplicate", $"{context}: agent.imports.{field} contains duplicate '{value.Id}'.");
                continue;
            }

            SemVersionRange? range = null;
            if (!string.IsNullOrWhiteSpace(value.Version) && !SemVersionRange.TryParse(value.Version, out range))
            {
                Error(issues, "agent.dependency.version.invalid",
                    $"{context}: agent.imports.{field} '{value.Id}' has invalid version range '{value.Version}'. " +
                    "Use an exact version or comparisons such as '>=1.0.0 <2.0.0'.");
                continue;
            }

            result.Add(new AgentAssetReference(value.Id, range));
        }

        return result;
    }

    /// <summary>An omitted or empty providers list means the asset is available for every provider.</summary>
    public static IReadOnlyList<ProviderName> MapProviders(List<string> values, string context, List<CatalogIssue> issues)
    {
        if (values.Count == 0) return ProviderNames.All;

        var providers = new List<ProviderName>();
        foreach (var value in values.SelectMany(SplitList))
        {
            if (ProviderNames.TryParse(value, out var provider))
            {
                if (!providers.Contains(provider)) providers.Add(provider);
            }
            else
            {
                Error(issues, "asset.provider.invalid",
                    $"{context}: unknown provider '{value}'. Valid providers: {string.Join(", ", ProviderNames.All.Select(ProviderNames.Display))}.");
            }
        }

        return providers.Count > 0 ? providers : ProviderNames.All;
    }

    private static AssetSource? MapSource(SourceDto? dto, string context, List<CatalogIssue> issues)
    {
        // No source at all: local content in the conventional content/ folder
        // (the layer loader fills RelativePath in from the asset directory).
        if (dto is null) return new AssetSource.Local("", null);

        if (!string.IsNullOrWhiteSpace(dto.Shorthand))
        {
            return MapShorthand(dto.Shorthand, context, issues);
        }

        var type = string.IsNullOrWhiteSpace(dto.Type)
            ? (string.IsNullOrWhiteSpace(dto.Url) && string.IsNullOrWhiteSpace(dto.Repo) ? "local" : "external")
            : dto.Type.Trim().ToLowerInvariant();

        switch (type)
        {
            case "local":
                return new AssetSource.Local(dto.Path ?? "", NullIfEmpty(dto.Checksum));

            case "external":
                var url = NullIfEmpty(dto.Url) ?? NullIfEmpty(dto.Repo);
                if (url is null)
                {
                    Error(issues, "asset.source.url.missing", $"{context}: external source needs a url.");
                    return null;
                }

                var reference = NullIfEmpty(dto.Ref);
                if (reference is null)
                {
                    Error(issues, "asset.source.ref.missing",
                        $"{context}: external source needs ref: with the reviewed commit SHA or immutable tag.");
                    return null;
                }

                return new AssetSource.External(url, reference, NullIfEmpty(dto.Path), NullIfEmpty(dto.Checksum), NullIfEmpty(dto.License));

            default:
                Error(issues, "asset.source.type.invalid", $"{context}: unknown source type '{dto.Type}'. Use local or external.");
                return null;
        }
    }

    /// <summary>Parses the one-line external form <c>https://github.com/owner/repo/tree/main/path@ref</c>.</summary>
    public static AssetSource? MapShorthand(string shorthand, string context, List<CatalogIssue> issues)
    {
        var text = shorthand.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        var at = text.LastIndexOf('@');
        string url;
        string? reference = null;
        if (at > schemeEnd + 3)
        {
            url = text[..at];
            reference = text[(at + 1)..];
        }
        else
        {
            url = text;
        }

        if (reference is null)
        {
            Error(issues, "asset.source.ref.missing",
                $"{context}: external source shorthand needs '@<commit-sha-or-tag>' at the end (e.g. {url}@8f3a91c...).");
            return null;
        }

        return new AssetSource.External(url, reference, Path: null, Checksum: null, License: null);
    }

    private static McpServer? MapMcp(AssetDto dto, string context, List<CatalogIssue> issues)
    {
        if (dto.Mcp is null) return null;
        var transport = TryEnum(() => EnumParsers.ParseTransport(dto.Mcp.Transport, context), McpTransport.Stdio, issues);

        if (transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(dto.Mcp.Command))
        {
            Error(issues, "asset.mcp.command.missing", $"{context}: stdio MCP server needs command:.");
        }

        if (transport != McpTransport.Stdio && string.IsNullOrWhiteSpace(dto.Mcp.Url))
        {
            Error(issues, "asset.mcp.url.missing", $"{context}: {transport.ToString().ToLowerInvariant()} MCP server needs url:.");
        }

        return new McpServer
        {
            Server = string.IsNullOrWhiteSpace(dto.Mcp.Server) ? dto.Id : dto.Mcp.Server,
            Transport = transport,
            Command = NullIfEmpty(dto.Mcp.Command),
            Args = dto.Mcp.Args,
            EnvVars = dto.Mcp.EnvVars,
            Url = NullIfEmpty(dto.Mcp.Url),
            Tools = dto.Mcp.Tools,
            Cwd = NullIfEmpty(dto.Mcp.Cwd),
            HeaderEnvVars = dto.Mcp.HeaderEnvVars
        };
    }

    private static HookSpec? MapHook(AssetDto dto, string context, List<CatalogIssue> issues)
    {
        if (dto.Hook is null) return null;
        var trigger = TryEnum(() => EnumParsers.ParseTrigger(dto.Hook.Trigger, context), HookTrigger.PreToolUse, issues);
        if (dto.Hook.TimeoutSec <= 0)
        {
            Error(issues, "asset.hook.timeout.invalid", $"{context}: hook timeoutSec must be positive.");
        }

        return new HookSpec
        {
            Trigger = trigger,
            Tool = NullIfEmpty(dto.Hook.Tool),
            Command = NullIfEmpty(dto.Hook.Command),
            TimeoutSec = dto.Hook.TimeoutSec > 0 ? dto.Hook.TimeoutSec : 30
        };
    }

    private static GroupDefinition? MapGroup(GroupDto dto, List<CatalogIssue> issues)
    {
        var context = $"group '{dto.Id}'";
        if (string.IsNullOrWhiteSpace(dto.Id) || !IdPattern.IsMatch(dto.Id))
        {
            Error(issues, "group.id.invalid", $"{context}: group ids must be kebab-case and non-empty.");
            return null;
        }

        return new GroupDefinition
        {
            Id = dto.Id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name,
            Status = TryEnum(() => EnumParsers.ParseGroupStatus(dto.Status, context), GroupStatus.Active, issues),
            ReplacedBy = NullIfEmpty(dto.ReplacedBy),
            RemoveAfter = NullIfEmpty(dto.RemoveAfter)
        };
    }

    private static ProfileDefinition? MapProfile(ProfileDto dto, List<BundleDto> bundles, List<CatalogIssue> issues)
    {
        var context = $"profile '{dto.Id}'";
        if (string.IsNullOrWhiteSpace(dto.Id) || !IdPattern.IsMatch(dto.Id))
        {
            Error(issues, "profile.id.invalid", $"{context}: profile ids must be kebab-case and non-empty.");
            return null;
        }

        // Migration path: bundles referenced by the profile are expanded into its asset list.
        var assets = new List<string>(dto.Assets);
        foreach (var bundleId in dto.Bundles)
        {
            var bundle = bundles.FirstOrDefault(x => x.Id.Equals(bundleId, StringComparison.OrdinalIgnoreCase));
            if (bundle is null)
            {
                Error(issues, "profile.bundle.unknown", $"{context}: references unknown bundle '{bundleId}' (bundles are removed — list assets directly).");
                continue;
            }

            foreach (var assetId in bundle.Assets)
            {
                if (!assets.Contains(assetId, StringComparer.OrdinalIgnoreCase)) assets.Add(assetId);
            }
        }

        return new ProfileDefinition
        {
            Id = dto.Id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name,
            Providers = MapProviders(dto.Providers, context, issues) is var p && dto.Providers.Count == 0 ? [] : p,
            Groups = dto.Groups,
            Assets = assets
        };
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static T TryEnum<T>(Func<T> parse, T fallback, List<CatalogIssue> issues)
    {
        try
        {
            return parse();
        }
        catch (AgentPackException ex)
        {
            Error(issues, "asset.enum.invalid", ex.Message);
            return fallback;
        }
    }

    private static void Error(List<CatalogIssue> issues, string code, string message) =>
        issues.Add(new CatalogIssue(IssueSeverity.Error, code, message));

    private static void Warning(List<CatalogIssue> issues, string code, string message) =>
        issues.Add(new CatalogIssue(IssueSeverity.Warning, code, message));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
