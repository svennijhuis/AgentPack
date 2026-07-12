namespace AgentPack.Core;

/// <summary>
/// Fetches external asset content into the content-addressed cache
/// (~/.agentpack/cache/external/&lt;key&gt;) and verifies checksums.
/// </summary>
public sealed class ExternalResolver
{
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

        var repo = ProcessRunner.SafeGitArg(resolved.Repo, "repository URL");
        var reference = ProcessRunner.SafeGitArg(resolved.Ref, "git ref");
        var repoCache = Path.Combine(_paths.ExternalCacheRoot, CacheKey(resolved), "repo");
        Directory.CreateDirectory(Path.GetDirectoryName(repoCache)!);
        if (!Directory.Exists(Path.Combine(repoCache, ".git")))
        {
            Ensure(ProcessRunner.Run("git", ["clone", "--", repo, repoCache], _paths.WorkingDirectory),
                asset, $"clone {resolved.Repo}");
        }
        else
        {
            Ensure(ProcessRunner.Run("git", ["fetch", "--all", "--tags", "--prune"], repoCache), asset, "fetch");
        }

        Ensure(ProcessRunner.Run("git", ["checkout", "--force", reference, "--"], repoCache), asset, $"checkout ref '{resolved.Ref}'");

        var sourcePath = Path.GetFullPath(Path.Combine(repoCache, resolved.Path));
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new AgentPackException(
                $"External path '{resolved.Path}' was not found in {resolved.Repo} at {resolved.Ref} for '{asset.Id}'.",
                "Check the URL path and the pinned ref.");
        }

        if (Directory.Exists(finalPath)) Directory.Delete(finalPath, recursive: true);
        if (File.Exists(finalPath)) File.Delete(finalPath);
        ContentHash.CopyTree(sourcePath, finalPath);

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
        ContentHash.ShortKey(resolved.Repo, resolved.Ref, resolved.Path, resolved.Checksum);

    private static void Ensure(ProcessResult result, Asset asset, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new AgentPackException(
                $"Unable to {operation} for '{asset.Id}': {FirstLine(result.Error)}",
                "Check network access, the repo URL, and your git credentials.");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";
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
            AssetSource.Local local => Path.GetFullPath(Path.Combine(loaded.RootFor(asset), local.RelativePath)),
            AssetSource.External => _externalResolver.ResolveToCache(ExternalResolver.WithEffectiveChecksum(loaded, asset)),
            _ => throw new AgentPackException($"Asset '{asset.Id}' has an unknown source type.")
        };
    }

    /// <summary>Cache-only resolution for planning: local assets resolve eagerly, external return null unless fetched.</summary>
    public string? TryResolve(LoadedCatalog loaded, Asset asset)
    {
        return asset.Source switch
        {
            AssetSource.Local local => Path.GetFullPath(Path.Combine(loaded.RootFor(asset), local.RelativePath)),
            AssetSource.External => _externalResolver.TryResolveFromCache(ExternalResolver.WithEffectiveChecksum(loaded, asset)),
            _ => null
        };
    }
}
