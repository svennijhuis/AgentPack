using AgentPack.Core.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentPack.Core;

public static class CatalogLoader
{
    // Unknown keys are ignored on purpose: a catalog written by a newer agentpack must
    // still load on an older one, otherwise minimumAgentPackVersion could never report
    // its upgrade message and a single contributor's stray key would break every command.
    // Keys that were genuinely removed are rejected explicitly in CatalogMapper instead.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new SourceDtoConverter())
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new SourceDtoConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public static CatalogDto LoadDto(string catalogPath)
    {
        if (!File.Exists(catalogPath))
        {
            throw new AgentPackException(
                $"Catalog file was not found: {catalogPath}",
                "Run from a catalog repo, or select one with 'agentpack catalog use <git-url>'.");
        }

        return Parse<CatalogDto>(catalogPath) ?? new CatalogDto();
    }

    public static AssetDto LoadAssetDto(string assetManifestPath)
    {
        if (!File.Exists(assetManifestPath))
        {
            throw new AgentPackException($"Asset manifest was not found: {assetManifestPath}");
        }

        return Parse<AssetDto>(assetManifestPath) ?? new AssetDto();
    }

    public static string ToYaml<T>(T value) => Serializer.Serialize(value);

    public static T FromYaml<T>(string yaml) => Deserializer.Deserialize<T>(yaml);

    private static T? Parse<T>(string path)
    {
        try
        {
            return Deserializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            throw new AgentPackException(
                $"{path} is not valid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {Root(ex).Message}",
                "Fix the YAML syntax and rerun 'agentpack catalog validate'.",
                ExitCodes.ValidationFailed);
        }
    }

    private static Exception Root(Exception ex) => ex.InnerException is null ? ex : Root(ex.InnerException);
}
