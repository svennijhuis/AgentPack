namespace AgentPack.Core;

public sealed class Installer
{
    private readonly AgentPackPaths _paths;
    private readonly AssetResolver _resolver;

    public Installer(AgentPackPaths paths)
    {
        _paths = paths;
        _resolver = new AssetResolver(paths);
    }

    public InstallPlan Plan(LoadedCatalog loaded, IEnumerable<Asset> assets, IEnumerable<ProviderName> providers, InstallScope scope)
    {
        var lockFile = JsonStore.Load<AgentPackLock>(_paths.GetLockPath(scope));
        var root = ScopeRoot(scope);
        var items = new List<InstallPlanItem>();
        var skipped = new List<SkippedInstall>();
        var providerList = providers.Distinct().ToList();

        foreach (var asset in assets)
        {
            foreach (var provider in providerList)
            {
                if (!asset.Providers.Contains(provider)) continue;

                switch (ProviderRegistry.Get(provider).Plan(asset, scope == InstallScope.User))
                {
                    case ProviderPlan.Unsupported unsupported:
                        skipped.Add(new SkippedInstall(asset, provider, unsupported.Reason));
                        continue;

                    case ProviderPlan.Supported supported:
                        var target = supported.Target;

                        // External assets resolve from cache only while planning; the clone happens in Apply.
                        var sourcePath = _resolver.TryResolve(loaded, asset);
                        var targetPath = Path.GetFullPath(Path.Combine(root, target.RelativePath));
                        if (sourcePath is not null && target.IsFileTarget)
                        {
                            sourcePath = ResolveFileSource(sourcePath, targetPath, asset.Id);
                        }

                        var existing = lockFile.Find(asset.Id, provider, asset.Kind);
                        items.Add(new InstallPlanItem(
                            asset, provider, sourcePath, targetPath, target,
                            DetermineState(asset, targetPath, existing, root), existing));
                        continue;
                }
            }
        }

        return new InstallPlan(items, skipped);
    }

    /// <summary>
    /// Applies plan items. Items in the LocalChanges state are decided by
    /// <paramref name="onDrift"/> (interactive prompt, or a flag-driven policy).
    /// </summary>
    public IReadOnlyList<ApplyResult> Apply(
        IEnumerable<InstallPlanItem> items,
        LoadedCatalog loaded,
        InstallScope scope,
        Func<InstallPlanItem, DriftAction> onDrift)
    {
        var lockPath = _paths.GetLockPath(scope);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var root = ScopeRoot(scope);
        var results = new List<ApplyResult>();

        foreach (var item in items)
        {
            if (item.Existing?.Pinned == true && item.Asset.Version.IsNewerThan(item.Existing.Version))
            {
                results.Add(new ApplyResult(item, ApplyOutcome.SkippedPinned));
                continue;
            }

            if (item.State == InstallState.LocalChanges && onDrift(item) == DriftAction.Keep)
            {
                results.Add(new ApplyResult(item, ApplyOutcome.KeptLocalChanges));
                continue;
            }

            if (item.State == InstallState.Installed)
            {
                results.Add(new ApplyResult(item, ApplyOutcome.AlreadyUpToDate));
                continue;
            }

            var sourcePath = ResolveApplySource(loaded, item);
            var installedChecksum = ApplyItem(item, sourcePath, root, scope);

            lockFile.Entries.RemoveAll(x =>
                x.Id.Equals(item.Asset.Id, StringComparison.OrdinalIgnoreCase) &&
                x.Provider == item.Provider &&
                x.Kind == item.Asset.Kind);

            lockFile.Entries.Add(new LockEntry
            {
                Id = item.Asset.Id,
                Kind = item.Asset.Kind,
                Provider = item.Provider,
                Version = item.Asset.Version.ToString(),
                Path = Path.GetRelativePath(root, item.TargetPath).Replace(Path.DirectorySeparatorChar, '/'),
                InstallMode = item.Target.Mode,
                SourceChecksum = SourceChecksum(loaded, item.Asset, sourcePath),
                InstalledChecksum = installedChecksum,
                Pinned = item.Existing?.Pinned ?? false
            });

            results.Add(new ApplyResult(item, item.Existing is null ? ApplyOutcome.Installed : ApplyOutcome.Updated));
        }

        JsonStore.Save(lockPath, lockFile);
        WriteScopeGitIgnore(scope);
        return results;
    }

    public List<LockEntry> Remove(AssetKind? kind, IReadOnlyList<string> ids, IReadOnlyList<ProviderName>? providers, InstallScope scope)
    {
        var lockPath = _paths.GetLockPath(scope);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var root = ScopeRoot(scope);

        var matches = lockFile.Entries.Where(entry =>
            (kind is null || entry.Kind == kind) &&
            (ids.Count == 0 || ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)) &&
            (providers is null or { Count: 0 } || providers.Contains(entry.Provider))).ToList();

        foreach (var entry in matches)
        {
            var installedPath = ResolveLockPath(entry.Path, root);
            if (entry.InstallMode is InstallMode.MergeMcp or InstallMode.MergeHook)
            {
                // Shared provider config files are never deleted; only the lock entry goes.
                lockFile.Entries.Remove(entry);
                continue;
            }

            if (File.Exists(installedPath) || Directory.Exists(installedPath))
            {
                Backup(installedPath, scope);
                DeleteExisting(installedPath);
            }

            lockFile.Entries.Remove(entry);
        }

        JsonStore.Save(lockPath, lockFile);
        return matches;
    }

    public InstallPlan Outdated(LoadedCatalog loaded, InstallScope scope, IReadOnlyList<ProviderName>? providers = null)
    {
        var lockFile = JsonStore.Load<AgentPackLock>(_paths.GetLockPath(scope));
        var assets = loaded.Catalog.Assets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var items = new List<InstallPlanItem>();
        var skipped = new List<SkippedInstall>();

        foreach (var entry in lockFile.Entries)
        {
            if (providers is { Count: > 0 } && !providers.Contains(entry.Provider)) continue;
            if (!assets.TryGetValue(entry.Id, out var asset)) continue;
            if (entry.Pinned) continue;
            if (asset.Version.IsNewerThan(entry.Version))
            {
                var plan = Plan(loaded, [asset], [entry.Provider], scope);
                items.AddRange(plan.Items);
                skipped.AddRange(plan.Skipped);
            }
        }

        return new InstallPlan(items, skipped);
    }

    public void SetPinned(string id, bool pinned, InstallScope scope)
    {
        var lockPath = _paths.GetLockPath(scope);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var entries = lockFile.Entries.Where(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count == 0)
        {
            throw new AgentPackException(
                $"No installed entry found for '{id}' in {(scope == InstallScope.User ? "user" : "project")} scope.",
                "Run 'agentpack status' to see what is installed.");
        }

        foreach (var entry in entries) entry.Pinned = pinned;
        JsonStore.Save(lockPath, lockFile);
    }

    public string ScopeRoot(InstallScope scope) => scope == InstallScope.User ? _paths.ProviderHome : _paths.WorkingDirectory;

    public static string ResolveLockPath(string entryPath, string root) =>
        Path.IsPathRooted(entryPath) ? entryPath : Path.GetFullPath(Path.Combine(root, entryPath));

    private static string ResolveFileSource(string sourcePath, string targetPath, string assetId)
    {
        if (File.Exists(sourcePath)) return sourcePath;

        var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
        var targetName = Path.GetFileName(targetPath);
        var exact = files.FirstOrDefault(x => Path.GetFileName(x).Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (files.Count == 1) return files[0];

        var extension = Path.GetExtension(targetPath);
        var byExtension = files.Where(x => Path.GetExtension(x).Equals(extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byExtension.Count == 1) return byExtension[0];

        throw new AgentPackException(
            $"Asset '{assetId}' installs to the single file '{targetName}', but its content folder has {files.Count} candidate files.",
            $"Keep exactly one file (or one named '{targetName}') in content/.");
    }

    private static InstallState DetermineState(Asset asset, string targetPath, LockEntry? existing, string root)
    {
        if (existing is null)
        {
            return File.Exists(targetPath) || Directory.Exists(targetPath)
                ? InstallState.UnmanagedPresent
                : InstallState.Available;
        }

        var installedPath = ResolveLockPath(existing.Path, root);
        if (!File.Exists(installedPath) && !Directory.Exists(installedPath)) return InstallState.Missing;

        var actual = ContentHash.Compute(installedPath);
        if (!actual.Equals(existing.InstalledChecksum, StringComparison.OrdinalIgnoreCase)) return InstallState.LocalChanges;
        if (asset.Version.IsNewerThan(existing.Version)) return existing.Pinned ? InstallState.Pinned : InstallState.UpdateAvailable;
        return InstallState.Installed;
    }

    private string ResolveApplySource(LoadedCatalog loaded, InstallPlanItem item)
    {
        if (item.Asset.Mcp is not null && item.Target.Mode == InstallMode.MergeMcp)
        {
            return item.SourcePath ?? "";
        }

        var sourcePath = item.SourcePath ?? _resolver.Resolve(loaded, item.Asset);
        if (item.Target.IsFileTarget)
        {
            sourcePath = ResolveFileSource(sourcePath, item.TargetPath, item.Asset.Id);
        }

        return sourcePath;
    }

    private string ApplyItem(InstallPlanItem item, string sourcePath, string root, InstallScope scope)
    {
        switch (item.Target.Mode)
        {
            case InstallMode.MergeMcp:
                return McpMerger.Apply(item.Asset, string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath,
                    item.Target, item.TargetPath, scope, path => Backup(path, scope));

            case InstallMode.MergeHook:
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    throw new AgentPackException($"Hook asset '{item.Asset.Id}' has no resolved content path.");
                }

                return HookMerger.Apply(item.Asset, sourcePath, item.Target, item.TargetPath, root, scope, path => Backup(path, scope));

            case InstallMode.CopyTree:
            default:
                if (File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath))
                {
                    Backup(item.TargetPath, scope);
                    DeleteExisting(item.TargetPath);
                }

                ContentHash.CopyTree(sourcePath, item.TargetPath);
                return ContentHash.Compute(item.TargetPath);
        }
    }

    private static string SourceChecksum(LoadedCatalog loaded, Asset asset, string sourcePath)
    {
        var declared = loaded.EffectiveChecksum(asset);
        if (declared is not null) return declared;

        return !string.IsNullOrWhiteSpace(sourcePath) && (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            ? ContentHash.Compute(sourcePath)
            : "";
    }

    private void Backup(string path, InstallScope scope)
    {
        var root = scope == InstallScope.User ? _paths.Home : Path.Combine(_paths.WorkingDirectory, ".agentpack");
        var backupRoot = Path.Combine(root, "backups", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        var target = Path.Combine(backupRoot, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        ContentHash.CopyTree(path, target);
    }

    private void WriteScopeGitIgnore(InstallScope scope)
    {
        if (scope == InstallScope.User) return;
        var directory = Path.Combine(_paths.WorkingDirectory, ".agentpack");
        var gitIgnore = Path.Combine(directory, ".gitignore");
        if (File.Exists(gitIgnore)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(gitIgnore, "backups/\n");
    }

    private static void DeleteExisting(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
