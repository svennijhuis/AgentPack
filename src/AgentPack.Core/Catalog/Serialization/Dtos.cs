using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace AgentPack.Core.Serialization;

// Mutable YAML-shaped DTOs. They exist only as the YamlDotNet target;
// CatalogMapper converts them into the immutable typed model and reports issues.

public sealed class CatalogDto
{
    public string SchemaVersion { get; set; } = "1";
    public string CatalogVersion { get; set; } = "0.1.0";
    public List<GroupDto> Groups { get; set; } = [];
    public List<AssetDto> Assets { get; set; } = [];
    public List<BundleDto> Bundles { get; set; } = [];
    public List<ProfileDto> Profiles { get; set; } = [];
}

public sealed class GroupDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "active";
    public string? ReplacedBy { get; set; }
    public string? RemoveAfter { get; set; }
}

/// <summary>Bundles are removed; kept in the DTO so old catalogs can be migrated with a warning.</summary>
public sealed class BundleDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public List<string> Assets { get; set; } = [];
    public List<string> Groups { get; set; } = [];
}

public sealed class ProfileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Providers { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<string> Bundles { get; set; } = [];
    public List<string> Assets { get; set; } = [];
}

public class AssetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Groups { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Providers { get; set; } = [];
    public string? Owner { get; set; }
    public string Status { get; set; } = "recommended";
    public string Channel { get; set; } = "stable";
    public SourceDto? Source { get; set; }
    public McpDto? Mcp { get; set; }
    public HookDto? Hook { get; set; }
}

/// <summary>
/// Asset source. Accepts either the mapping form or the single-line shorthand
/// <c>source: https://github.com/owner/repo/tree/main/path@ref</c>.
/// </summary>
public class SourceDto
{
    public string? Type { get; set; }
    public string? Url { get; set; }
    public string? Repo { get; set; }
    public string? Ref { get; set; }
    public string? Path { get; set; }
    public string? Checksum { get; set; }
    public string? License { get; set; }

    /// <summary>Set when the YAML used the single-scalar shorthand.</summary>
    public string? Shorthand { get; set; }
}

/// <summary>Marker subclass so the type converter can delegate mapping-form parsing without recursing.</summary>
public sealed class SourceDtoFields : SourceDto;

public sealed class McpDto
{
    public string Server { get; set; } = "";
    public string Transport { get; set; } = "stdio";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public List<string> EnvVars { get; set; } = [];
    public string Url { get; set; } = "";
    public List<string> Tools { get; set; } = [];
    public string Cwd { get; set; } = "";
    public Dictionary<string, string> HeaderEnvVars { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HookDto
{
    public string Trigger { get; set; } = "";
    public string Tool { get; set; } = "";
    public string Command { get; set; } = "";
    public int TimeoutSec { get; set; } = 30;
}

/// <summary>Parses <c>source:</c> as either a scalar shorthand or a normal mapping.</summary>
public sealed class SourceDtoConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(SourceDto);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Accept<Scalar>(out _))
        {
            var scalar = parser.Consume<Scalar>();
            return new SourceDto { Shorthand = scalar.Value };
        }

        return (SourceDtoFields)rootDeserializer(typeof(SourceDtoFields))!;
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        var source = (SourceDto?)value;
        if (source is null) return;
        if (!string.IsNullOrWhiteSpace(source.Shorthand))
        {
            emitter.Emit(new Scalar(source.Shorthand));
            return;
        }

        var fields = new SourceDtoFields
        {
            Type = source.Type,
            Url = source.Url,
            Repo = source.Repo,
            Ref = source.Ref,
            Path = source.Path,
            Checksum = source.Checksum,
            License = source.License
        };
        serializer(fields, typeof(SourceDtoFields));
    }
}
