using AgentPack.Core.Serialization;

namespace AgentPack.Core;

/// <summary>
/// Loads the effective catalog: the primary catalog (working directory or synced source),
/// asset manifests discovered under assets/, and the optional project overlay
/// (.agentpack/catalog.yaml + .agentpack/assets/). Derivable fields (id, kind, content path)
/// are inferred from the folder layout before mapping, so authors never repeat them.
/// </summary>
public sealed class CatalogLayerLoader
{
    private readonly SourceManager _sources;
    private readonly AgentPackPaths _paths;

    public CatalogLayerLoader(SourceManager sources, AgentPackPaths paths)
    {
        _sources = sources;
        _paths = paths;
    }

    public LoadedCatalog Load(string? explicitCatalogPath = null)
    {
        var issues = new List<CatalogIssue>();
        var basePath = _sources.ResolveCatalogPath(explicitCatalogPath);
        var baseDto = CatalogLoader.LoadDto(basePath);
        var baseRoot = Path.GetDirectoryName(Path.GetFullPath(basePath))!;
        var roots = new List<string> { baseRoot };
        DiscoverAssets(baseDto, baseRoot);

        var overlayPath = Path.Combine(_paths.WorkingDirectory, ".agentpack", "catalog.yaml");
        if (File.Exists(overlayPath) && !Path.GetFullPath(overlayPath).Equals(Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
        {
            var overlayDto = CatalogLoader.LoadDto(overlayPath);
            var overlayRoot = Path.GetDirectoryName(Path.GetFullPath(overlayPath))!;
            DiscoverAssets(overlayDto, overlayRoot);
            Merge(baseDto, overlayDto);
            roots.Add(overlayRoot);
        }

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
        return new LoadedCatalog(catalog, basePath, roots, lockFile, issues.Where(x => x.Severity == IssueSeverity.Warning).ToList());
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

    private static void Merge(CatalogDto target, CatalogDto overlay)
    {
        foreach (var group in overlay.Groups) Upsert(target.Groups, group, x => x.Id);
        foreach (var asset in overlay.Assets) Upsert(target.Assets, asset, x => x.Id);
        foreach (var bundle in overlay.Bundles) Upsert(target.Bundles, bundle, x => x.Id);
        foreach (var profile in overlay.Profiles) Upsert(target.Profiles, profile, x => x.Id);
    }

    private static void Upsert<T>(List<T> target, T item, Func<T, string> key)
    {
        var existing = target.FindIndex(x => key(x).Equals(key(item), StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) target[existing] = item;
        else target.Add(item);
    }
}

public sealed record LoadedCatalog(
    Catalog Catalog,
    string PrimaryCatalogPath,
    IReadOnlyList<string> CatalogRoots,
    CatalogLockFile Lock,
    IReadOnlyList<CatalogIssue> Warnings)
{
    public string RootFor(Asset asset)
    {
        if (asset.Source is AssetSource.Local local)
        {
            foreach (var root in CatalogRoots.Reverse())
            {
                var candidate = Path.GetFullPath(Path.Combine(root, local.RelativePath));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return root;
            }
        }

        return CatalogRoots[0];
    }

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
