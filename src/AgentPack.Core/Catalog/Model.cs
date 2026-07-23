using AgentPack.Core.Primitives;

namespace AgentPack.Core;

/// <summary>
/// Immutable, fully-parsed catalog. Produced once by <see cref="Serialization.CatalogMapper"/>;
/// nothing past the parsing boundary works with raw strings for kinds, providers, statuses, or versions.
/// </summary>
public sealed record Catalog
{
    public string SchemaVersion { get; init; } = "1";
    public string CatalogVersion { get; init; } = "0.1.0";
    public SemVersion? MinimumAgentPackVersion { get; init; }
    public IReadOnlyList<GroupDefinition> Groups { get; init; } = [];
    public IReadOnlyList<Asset> Assets { get; init; } = [];
    public IReadOnlyList<ProfileDefinition> Profiles { get; init; } = [];
}

public sealed record GroupDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public GroupStatus Status { get; init; } = GroupStatus.Active;
    public string? ReplacedBy { get; init; }
    public string? RemoveAfter { get; init; }
}

public sealed record ProfileDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<ProviderName> Providers { get; init; } = [];
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Assets { get; init; } = [];
}

public sealed record Asset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AssetKind Kind { get; init; }
    public required SemVersion Version { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Always non-empty: an omitted providers list means "every provider".</summary>
    public required IReadOnlyList<ProviderName> Providers { get; init; }

    public string? Owner { get; init; }
    public AssetStatus Status { get; init; } = AssetStatus.Recommended;
    public Channel Channel { get; init; } = Channel.Stable;
    public required AssetSource Source { get; init; }
    public McpServer? Mcp { get; init; }
    public HookSpec? Hook { get; init; }
}

/// <summary>
/// Where asset content comes from — a closed hierarchy, switched exhaustively.
/// </summary>
public abstract record AssetSource
{
    private AssetSource() { }

    /// <summary>Content lives in the catalog repo, by convention in the asset's content/ folder.</summary>
    public sealed record Local(string RelativePath, string? Checksum) : AssetSource;

    /// <summary>Content lives in an external git repo, pinned to an immutable ref with its license declaration.</summary>
    public sealed record External(
        string Url,
        string Ref,
        string? Path,
        string? Checksum,
        string? License) : AssetSource;
}

public sealed record McpServer
{
    public required string Server { get; init; }
    public McpTransport Transport { get; init; } = McpTransport.Stdio;
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<string> EnvVars { get; init; } = [];
    public string? Url { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    public string? Cwd { get; init; }
    public IReadOnlyDictionary<string, string> HeaderEnvVars { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record HookSpec
{
    public HookTrigger Trigger { get; init; } = HookTrigger.PreToolUse;
    public string? Tool { get; init; }
    public string? Command { get; init; }
    public int TimeoutSec { get; init; } = 30;
}

public sealed record CatalogIssue(IssueSeverity Severity, string Code, string Message);

public enum IssueSeverity
{
    Warning,
    Error
}
