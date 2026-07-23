namespace AgentPack.Core;

/// <summary>
/// Fetches external asset content into the content-addressed cache
/// (~/.agentpack/cache/external/&lt;key&gt;) and verifies checksums.
/// </summary>
public sealed class ExternalResolver
{
    private static readonly GitRunner Git = new("Check network access, the repo URL, and your git credentials.");

    private readonly AgentPackPaths _paths;

    public ExternalResolver(AgentPackPaths paths)
    {
        _paths = paths;
    }

    public string? TryResolveFromCache(Asset asset)
    {
        var source = RequireExternal(asset);
        var resolved = ExternalSourceParser.Resolve(source);
        var finalPath = CachedContentPath(resolved);
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            var cachedHash = ContentHash.Compute(finalPath);
            if (resolved.Checksum is null || cachedHash.Equals(resolved.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                return finalPath;
            }
        }

        return null;
    }

    public string ResolveToCache(Asset asset)
    {
        var cached = TryResolveFromCache(asset);
        if (cached is not null) return cached;

        var source = RequireExternal(asset);
        var resolved = ExternalSourceParser.Resolve(source);
        var finalPath = CachedContentPath(resolved);

        // Two agentpack processes resolving the same asset would race on the same
        // delete-then-copy below; serialize per cache entry, then re-check the cache.
        var cacheRoot = Path.Combine(_paths.ExternalCacheRoot, CacheKey(resolved));
        using var cacheLock = ScopeLock.Acquire(cacheRoot);
        cached = TryResolveFromCache(asset);
        if (cached is not null) return cached;

        var repo = ProcessRunner.SafeGitArg(resolved.Repo, "repository URL");
        var reference = ProcessRunner.SafeGitArg(resolved.Ref, "git ref");
        var repoCache = Path.Combine(cacheRoot, "repo");
        if (!Directory.Exists(Path.Combine(repoCache, ".git")))
        {
            Git.Run(["clone", "--", repo, repoCache], _paths.WorkingDirectory,
                $"Unable to clone {resolved.Repo} for '{asset.Id}'");
        }
        else
        {
            Git.Run(["fetch", "--all", "--tags", "--prune"], repoCache, $"Unable to fetch for '{asset.Id}'");
        }

        Git.Run(["checkout", "--force", reference, "--"], repoCache, $"Unable to checkout ref '{resolved.Ref}' for '{asset.Id}'");

        var sourcePath = Path.GetFullPath(Path.Combine(repoCache, resolved.Path));
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new AgentPackException(
                $"External path '{resolved.Path}' was not found in {resolved.Repo} at {resolved.Ref} for '{asset.Id}'.",
                "Check the URL path and the pinned ref.");
        }

        if (Directory.Exists(finalPath)) Directory.Delete(finalPath, recursive: true);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        CopyReviewedContent(sourcePath, finalPath, asset.Id);

        var actual = ContentHash.Compute(finalPath);
        if (resolved.Checksum is not null && !actual.Equals(resolved.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(finalPath, recursive: true);
            throw new AgentPackException(
                $"External checksum mismatch for '{asset.Id}': expected {resolved.Checksum}, got {actual}.",
                "The upstream content changed under the pinned ref, or the lock entry is stale. Re-review the upstream and rerun 'agentpack catalog lock'.",
                ExitCodes.DriftOrConflict);
        }

        return finalPath;
    }

    public ValidationReport VerifyExternal(LoadedCatalog loaded)
    {
        var report = new ValidationReport();
        foreach (var asset in loaded.Catalog.Assets.Where(x => x.Source is AssetSource.External))
        {
            try
            {
                ResolveToCache(WithEffectiveChecksum(loaded, asset));
            }
            catch (AgentPackException ex)
            {
                report.Error("external.verify.failed", $"{asset.Id}: {ex.Message}");
            }
        }

        return report;
    }

    /// <summary>Folds the catalog.lock.yaml checksum into the asset so cache verification can use it.</summary>
    public static Asset WithEffectiveChecksum(LoadedCatalog loaded, Asset asset)
    {
        if (asset.Source is not AssetSource.External external || external.Checksum is not null) return asset;
        var effective = loaded.EffectiveChecksum(asset);
        return effective is null ? asset : asset with { Source = external with { Checksum = effective } };
    }

    private static AssetSource.External RequireExternal(Asset asset)
    {
        return asset.Source as AssetSource.External
            ?? throw new AgentPackException($"Asset '{asset.Id}' is not external.");
    }

    private string CachedContentPath(ResolvedExternalSource resolved) =>
        Path.Combine(_paths.ExternalCacheRoot, CacheKey(resolved), "content");

    private static string CacheKey(ResolvedExternalSource resolved) =>
        // The checksum verifies this identity; it is not part of the identity. Keeping
        // one cache entry lets a newly generated lock verify the exact bytes it hashed.
        ContentHash.ShortKey(resolved.Repo, resolved.Ref, resolved.Path);

    private static void CopyReviewedContent(string source, string destination, string assetId)
    {
        if (File.Exists(source))
        {
            SafeTree.Attributes(source, $"External asset '{assetId}'");
            ContentHash.CopyTree(source, destination);
            return;
        }

        Directory.CreateDirectory(destination);
        CopyDirectory(source, source, destination, assetId);
    }

    private static void CopyDirectory(string root, string directory, string destination, string assetId)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            var relative = Path.GetRelativePath(root, entry);
            var attributes = SafeTree.Attributes(entry, $"External asset '{assetId}' path '{relative}'");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (name is ".git" or ".hg" or ".svn") continue;
                Directory.CreateDirectory(Path.Combine(destination, relative));
                CopyDirectory(root, entry, destination, assetId);
                continue;
            }

            var target = Path.Combine(destination, relative);
            ContentHash.CopyTree(entry, target);
        }
    }

}

/// <summary>Resolves any asset's content to a local path (catalog folder or external cache).</summary>
public sealed class AssetResolver
{
    private readonly ExternalResolver _externalResolver;

    public AssetResolver(AgentPackPaths paths)
    {
        _externalResolver = new ExternalResolver(paths);
    }

    public string Resolve(LoadedCatalog loaded, Asset asset)
    {
        return asset.Source switch
        {
            AssetSource.Local local => Path.GetFullPath(Path.Combine(loaded.CatalogRoot, local.RelativePath)),
            AssetSource.External => _externalResolver.ResolveToCache(ExternalResolver.WithEffectiveChecksum(loaded, asset)),
            _ => throw new AgentPackException($"Asset '{asset.Id}' has an unknown source type.")
        };
    }

    /// <summary>Cache-only resolution for planning: local assets resolve eagerly, external return null unless fetched.</summary>
    public string? TryResolve(LoadedCatalog loaded, Asset asset)
    {
        return asset.Source switch
        {
            AssetSource.Local local => Path.GetFullPath(Path.Combine(loaded.CatalogRoot, local.RelativePath)),
            AssetSource.External => _externalResolver.TryResolveFromCache(ExternalResolver.WithEffectiveChecksum(loaded, asset)),
            _ => null
        };
    }
}
