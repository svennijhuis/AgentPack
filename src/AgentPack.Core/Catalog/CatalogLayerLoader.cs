using AgentPack.Core.Serialization;

namespace AgentPack.Core;

/// <summary>
/// Loads the active catalog (a checkout or synced source) and discovers manifests
/// under assets/. Derivable fields are inferred from the folder layout.
/// </summary>
public sealed class CatalogLayerLoader
{
    private readonly SourceManager _sources;

    public CatalogLayerLoader(SourceManager sources)
    {
        _sources = sources;
    }

    public LoadedCatalog Load(string? explicitCatalogPath = null, bool refreshRemoteNow = false)
    {
        var issues = new List<CatalogIssue>();
        var basePath = _sources.ResolveCatalogPath(explicitCatalogPath);
        if (_sources.RefreshIfStale(basePath, refreshRemoteNow) is { } staleWarning) issues.Add(staleWarning);
        var baseDto = CatalogLoader.LoadDto(basePath);
        var baseRoot = Path.GetDirectoryName(Path.GetFullPath(basePath))!;
        DiscoverAssets(baseDto, baseRoot);

        var catalog = CatalogMapper.Map(baseDto, issues);
        var errors = issues.Where(x => x.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new AgentPackException(
                "The catalog has errors:\n" + string.Join("\n", errors.Select(x => $"  [{x.Code}] {x.Message}")),
                "Fix the manifests and rerun 'agentpack catalog validate'.",
                ExitCodes.ValidationFailed);
        }

        var lockFile = CatalogLockFile.Load(CatalogLockFile.PathFor(basePath));
        return new LoadedCatalog(catalog, basePath, baseRoot, lockFile, issues.Where(x => x.Severity == IssueSeverity.Warning).ToList());
    }

    private static void DiscoverAssets(CatalogDto catalog, string root)
    {
        var assetsRoot = Path.Combine(root, "assets");
        if (!Directory.Exists(assetsRoot)) return;

        foreach (var manifestPath in Directory.EnumerateFiles(assetsRoot, "agentpack.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var asset = CatalogLoader.LoadAssetDto(manifestPath);
            InferFromFolder(asset, manifestPath, root);
            Upsert(catalog.Assets, asset, x => x.Id);
        }
    }

    /// <summary>Fills in everything derivable from the asset's location on disk.</summary>
    public static void InferFromFolder(AssetDto asset, string manifestPath, string root)
    {
        var assetDirectory = Path.GetDirectoryName(manifestPath)!;
        var relativeDirectory = Path.GetRelativePath(root, assetDirectory).Replace(Path.DirectorySeparatorChar, '/');
        var parts = relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (string.IsNullOrWhiteSpace(asset.Kind) && parts.Length >= 2) asset.Kind = parts[1];
        if (string.IsNullOrWhiteSpace(asset.Id)) asset.Id = Path.GetFileName(assetDirectory);
        if (string.IsNullOrWhiteSpace(asset.Name)) asset.Name = asset.Id;
        if (string.IsNullOrWhiteSpace(asset.Version)) asset.Version = "1.0.0";

        var isLocal = asset.Source is null ||
                      (string.IsNullOrWhiteSpace(asset.Source.Shorthand) &&
                       string.IsNullOrWhiteSpace(asset.Source.Url) &&
                       string.IsNullOrWhiteSpace(asset.Source.Repo) &&
                       !string.Equals(asset.Source.Type, "external", StringComparison.OrdinalIgnoreCase));

        if (isLocal)
        {
            asset.Source ??= new SourceDtoFields();
            asset.Source.Type = "local";
            if (string.IsNullOrWhiteSpace(asset.Source.Path))
            {
                var contentDirectory = Path.Combine(assetDirectory, "content");
                asset.Source.Path = Directory.Exists(contentDirectory)
                    ? Path.GetRelativePath(root, contentDirectory).Replace(Path.DirectorySeparatorChar, '/')
                    : relativeDirectory;
            }
        }
    }

    private static void Upsert<T>(List<T> target, T item, Func<T, string> key)
    {
        var existing = target.FindIndex(x => key(x).Equals(key(item), StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) target[existing] = item;
        else target.Add(item);
    }
}

/// <summary>
/// One catalog, one content root. Assets used to be able to come from a project overlay
/// as well, which is why local paths were resolved by probing a list of roots.
/// </summary>
public sealed record LoadedCatalog(
    Catalog Catalog,
    string PrimaryCatalogPath,
    string CatalogRoot,
    CatalogLockFile Lock,
    IReadOnlyList<CatalogIssue> Warnings)
{
    /// <summary>Manifest checksum wins; the generated catalog lock is the fallback.</summary>
    public string? EffectiveChecksum(Asset asset)
    {
        var manifestChecksum = asset.Source switch
        {
            AssetSource.Local local => local.Checksum,
            AssetSource.External external => external.Checksum,
            _ => null
        };

        return manifestChecksum ?? Lock.Find(asset.Id)?.Checksum;
    }
}
