namespace AgentPack.Core;

/// <summary>
/// Generates catalog.lock.yaml: content checksums for every asset, so manifests
/// never carry hand-maintained hashes. Run by catalog-repo CI on every merge.
/// </summary>
public sealed class CatalogLockWriter
{
    private readonly AgentPackPaths _paths;

    public CatalogLockWriter(AgentPackPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// <paramref name="ContentPaths"/> is the resolved content directory per asset id.
    /// Generating a lock already fetches and hashes every asset, so a caller that needs
    /// to inspect that content can reuse these instead of resolving (and re-hashing) it.
    /// </summary>
    public sealed record Result(
        CatalogLockFile Lock,
        IReadOnlyList<string> Messages,
        IReadOnlyDictionary<string, string> ContentPaths);

    public Result Generate(LoadedCatalog loaded, bool fetchExternal)
    {
        var lockFile = new CatalogLockFile();
        var messages = new List<string>();
        var contentPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolver = new ExternalResolver(_paths);

        foreach (var asset in loaded.Catalog.Assets.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            switch (asset.Source)
            {
                case AssetSource.Local local:
                    {
                        var contentPath = Path.GetFullPath(Path.Combine(loaded.CatalogRoot, local.RelativePath));
                        if (!File.Exists(contentPath) && !Directory.Exists(contentPath))
                        {
                            messages.Add($"{asset.Id}: local content missing at {local.RelativePath} — skipped.");
                            continue;
                        }

                        contentPaths[asset.Id] = contentPath;
                        lockFile.Entries.Add(new CatalogLockEntry
                        {
                            Id = asset.Id,
                            Kind = asset.Kind.Display(),
                            SourceType = "local",
                            Checksum = ContentHash.Compute(contentPath)
                        });
                        break;
                    }

                case AssetSource.External external:
                    {
                        var entry = new CatalogLockEntry
                        {
                            Id = asset.Id,
                            Kind = asset.Kind.Display(),
                            SourceType = "external",
                            Url = external.Url,
                            Ref = external.Ref
                        };

                        if (fetchExternal)
                        {
                            // Checksum the exact content at the pinned ref so installs can detect tampering.
                            var bare = asset with { Source = external with { Checksum = null } };
                            var contentPath = resolver.ResolveToCache(bare);
                            contentPaths[asset.Id] = contentPath;
                            entry.Checksum = ContentHash.Compute(contentPath);
                        }
                        else
                        {
                            messages.Add($"{asset.Id}: external checksum not computed (--no-fetch).");
                        }

                        lockFile.Entries.Add(entry);
                        break;
                    }
            }
        }

        return new Result(lockFile, messages, contentPaths);
    }
}
