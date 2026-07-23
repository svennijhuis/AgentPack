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
        SemVersion? minimumAgentPackVersion = null;
        if (!string.IsNullOrWhiteSpace(dto.MinimumAgentPackVersion))
        {
            if (SemVersion.TryParse(dto.MinimumAgentPackVersion, out var parsedMinimum))
            {
                minimumAgentPackVersion = parsedMinimum;
            }
            else
            {
                Error(issues, "catalog.minimumAgentPackVersion.invalid",
                    $"minimumAgentPackVersion '{dto.MinimumAgentPackVersion}' is not valid semver (expected MAJOR.MINOR.PATCH).");
            }
        }

        if (dto.Bundles is not null)
        {
            Error(issues, "catalog.bundles.removed",
                "'bundles:' was removed from the catalog schema. List the assets directly on each profile and delete the bundles block.");
        }

        var groups = dto.Groups.Select(g => MapGroup(g, issues)).Where(g => g is not null).Select(g => g!).ToList();
        var assets = dto.Assets.Select(a => MapAsset(a, issues)).Where(a => a is not null).Select(a => a!).ToList();
        var profiles = dto.Profiles.Select(p => MapProfile(p, issues)).Where(p => p is not null).Select(p => p!).ToList();

        return new Catalog
        {
            SchemaVersion = dto.SchemaVersion,
            CatalogVersion = dto.CatalogVersion,
            MinimumAgentPackVersion = minimumAgentPackVersion,
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
            Hook = hook
        };
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

                return new AssetSource.External(
                    url,
                    reference,
                    NullIfEmpty(dto.Path),
                    NullIfEmpty(dto.Checksum),
                    NullIfEmpty(dto.License));

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

    private static ProfileDefinition? MapProfile(ProfileDto dto, List<CatalogIssue> issues)
    {
        var context = $"profile '{dto.Id}'";
        if (string.IsNullOrWhiteSpace(dto.Id) || !IdPattern.IsMatch(dto.Id))
        {
            Error(issues, "profile.id.invalid", $"{context}: profile ids must be kebab-case and non-empty.");
            return null;
        }

        if (dto.Bundles is not null)
        {
            Error(issues, "profile.bundles.removed",
                $"{context}: 'bundles:' was removed. List the bundle's assets directly under the profile's assets:.");
        }

        return new ProfileDefinition
        {
            Id = dto.Id,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Id : dto.Name,
            Providers = MapProviders(dto.Providers, context, issues) is var p && dto.Providers.Count == 0 ? [] : p,
            Groups = dto.Groups,
            Assets = dto.Assets
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

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
