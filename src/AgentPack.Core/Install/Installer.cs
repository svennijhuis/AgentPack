namespace AgentPack.Core;

public sealed class Installer
{
    private readonly AgentPackPaths _paths;
    private readonly AssetResolver _resolver;
    private readonly AgentRenderer _agentRenderer;

    public Installer(AgentPackPaths paths)
    {
        _paths = paths;
        _resolver = new AssetResolver(paths);
        _agentRenderer = new AgentRenderer(paths);
    }

    public InstallPlan Plan(
        LoadedCatalog loaded,
        IEnumerable<Asset> assets,
        IEnumerable<ProviderName> providers,
        InstallScope scope,
        string? requestedBy = null)
    {
        var lockFile = JsonStore.Load<AgentPackLock>(_paths.GetLockPath(scope));
        var root = ScopeRoot(scope);
        var items = new List<InstallPlanItem>();
        var skipped = new List<SkippedInstall>();
        var providerList = providers.Distinct().ToList();

        var requested = assets.ToList();
        var dependencyResolver = new AgentDependencyResolver(loaded.Catalog);
        var planned = new Dictionary<(string Id, AssetKind Kind, ProviderName Provider), (Asset Asset, bool Direct, HashSet<string> RequiredBy)>();

        foreach (var asset in requested)
        {
            foreach (var provider in providerList)
            {
                if (!asset.Providers.Contains(provider)) continue;

                if (asset.Kind == AssetKind.Agents)
                {
                    var dependencies = dependencyResolver.Resolve(asset, provider);
                    foreach (var dependency in dependencies.Skills)
                    {
                        AddPlanned(dependency, provider, direct: false, $"agent:{asset.Id}");
                    }
                    // Cursor subagents inherit parent MCP servers, so these are the only
                    // agent imports with a shared/global install target.
                    if (provider == ProviderName.Cursor)
                    {
                        foreach (var dependency in dependencies.Mcp)
                        {
                            AddPlanned(dependency, provider, direct: false, $"agent:{asset.Id}");
                        }
                    }
                }

                AddPlanned(asset, provider, direct: requestedBy is null, requestedBy);
            }
        }

        // If a planned shared dependency must move, every installed agent that
        // owns it must be rebuilt in the same connected transaction.
        var byId = loaded.Catalog.Assets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var expanded = true;
        while (expanded)
        {
            expanded = false;
            foreach (var (key, entry) in planned.ToList())
            {
                if (entry.Asset.Kind == AssetKind.Agents) continue;
                var installed = lockFile.Find(entry.Asset.Id, key.Provider, entry.Asset.Kind);
                var effectiveChecksum = EffectiveSourceChecksum(loaded, entry.Asset, _resolver.TryResolve(loaded, entry.Asset));
                var dependencyChanged = entry.Asset.Version.IsNewerThan(installed?.Version ?? "") ||
                    effectiveChecksum is not null && !effectiveChecksum.Equals(installed?.SourceChecksum, StringComparison.OrdinalIgnoreCase);
                if (installed is null || !dependencyChanged) continue;
                foreach (var owner in installed.RequiredBy.Where(x => x.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)))
                {
                    var agentId = owner["agent:".Length..];
                    if (!byId.TryGetValue(agentId, out var dependentAgent) || dependentAgent.Kind != AssetKind.Agents) continue;
                    var agentLock = lockFile.Find(agentId, key.Provider, AssetKind.Agents);
                    var count = planned.Count;
                    var dependencies = dependencyResolver.Resolve(dependentAgent, key.Provider);
                    foreach (var dependency in dependencies.Skills)
                        AddPlanned(dependency, key.Provider, direct: false, $"agent:{agentId}");
                    if (key.Provider == ProviderName.Cursor)
                    {
                        foreach (var dependency in dependencies.Mcp)
                            AddPlanned(dependency, key.Provider, direct: false, $"agent:{agentId}");
                    }
                    AddPlanned(dependentAgent, key.Provider, agentLock?.Direct ?? false, null);
                    foreach (var provenance in agentLock?.RequiredBy ?? [])
                        AddPlanned(dependentAgent, key.Provider, direct: false, provenance);
                    expanded |= planned.Count != count;
                }
            }
        }

        foreach (var (key, entry) in planned)
        {
            var asset = entry.Asset;
            var provider = key.Provider;

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
                    if (sourcePath is not null && target.IsFileTarget && target.Mode != InstallMode.RenderAgent)
                    {
                        sourcePath = ResolveFileSource(sourcePath, targetPath, asset.Id);
                    }

                    var existing = lockFile.Find(asset.Id, provider, asset.Kind);
                    var fingerprint = asset.Kind == AssetKind.Agents
                        ? _agentRenderer.Fingerprint(loaded, asset, provider, scope)
                        : null;
                    var sourceChecksum = EffectiveSourceChecksum(loaded, asset, sourcePath);
                    var state = DetermineState(asset, targetPath, target, existing, root, scope, fingerprint, sourceChecksum, sourcePath);
                    if (state == InstallState.UnmanagedPresent && CandidateMatches(loaded, asset, provider, scope, target, sourcePath, targetPath))
                        state = InstallState.Adoptable;
                    items.Add(new InstallPlanItem(
                        asset, provider, sourcePath, targetPath, target,
                        state, existing,
                        entry.Direct, entry.RequiredBy.Order(StringComparer.OrdinalIgnoreCase).ToList(), fingerprint));
                    continue;
            }
        }

        return new InstallPlan(items, skipped);

        void AddPlanned(Asset candidate, ProviderName provider, bool direct, string? requiredBy)
        {
            var key = (candidate.Id.ToLowerInvariant(), candidate.Kind, provider);
            if (!planned.TryGetValue(key, out var value))
            {
                value = (candidate, direct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                planned[key] = value;
            }
            else if (direct && !value.Direct)
            {
                value.Direct = true;
                planned[key] = value;
            }
            if (requiredBy is not null) value.RequiredBy.Add(requiredBy);
        }
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
        var itemList = items.ToList();
        using var stagedCandidates = new TemporaryDirectory("agentpack-stage-");
        // Fetch and verify all sources, and render agents, before taking the scope lock
        // or changing a provider file. This is the transaction's staging phase.
        var sources = new Dictionary<InstallPlanItem, string>();
        var renderedAgents = new Dictionary<InstallPlanItem, string>();
        foreach (var item in itemList)
        {
            if (item.Existing?.Pinned == true && item.State == InstallState.Pinned)
            {
                var owner = item.RequiredBy?.FirstOrDefault();
                if (owner is not null)
                {
                    throw new AgentPackException(
                        $"[agent.dependency.pinned] Agent '{owner["agent:".Length..]}' requires '{item.Asset.Id}' {item.Asset.Version}, but the installed dependency is pinned at {item.Existing.Version}.",
                        $"Run 'agentpack unpin {item.Asset.Id}' and retry, or change the agent dependency requirement.",
                        ExitCodes.DriftOrConflict);
                }
                continue;
            }
            if (item.State is InstallState.Installed or InstallState.Pinned) continue;
            sources[item] = ResolveApplySource(loaded, item);
            if (item.Target.Mode == InstallMode.RenderAgent)
            {
                renderedAgents[item] = _agentRenderer.Render(loaded, item.Asset, item.Provider, scope);
                CatalogCompiler.ValidateSyntax(renderedAgents[item], item.Asset, item.Provider);
                var candidatePath = stagedCandidates.PathFor(item);
                AtomicWrite.Text(candidatePath, renderedAgents[item]);
            }
        }

        // An external candidate may not have been cached during the network-free
        // plan. Once staged, byte-identical unmanaged files can still be adopted.
        var stagedAdoptions = itemList
            .Where(x => x.State == InstallState.UnmanagedPresent && StagedCandidateMatches(x))
            .ToHashSet();
        var decisions = new Dictionary<InstallPlanItem, DriftAction>();
        var skippedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in itemList.Where(x =>
                     x.State is InstallState.LocalChanges or InstallState.UnmanagedPresent && !stagedAdoptions.Contains(x)))
        {
            var decisionItem = renderedAgents.ContainsKey(item)
                ? item with { StagedCandidatePath = stagedCandidates.PathFor(item) }
                : item with { StagedCandidatePath = sources.GetValueOrDefault(item) };
            var decision = onDrift(decisionItem);
            decisions[item] = decision;
            if (decision == DriftAction.Keep)
            {
                foreach (var owner in Owners(item)) skippedOwners.Add(owner);
            }
        }
        var propagated = true;
        while (propagated)
        {
            propagated = false;
            foreach (var item in itemList.Where(x => Owners(x).Any(skippedOwners.Contains)))
            {
                foreach (var owner in Owners(item)) propagated |= skippedOwners.Add(owner);
            }
        }

        var lockPath = _paths.GetLockPath(scope);
        using var scopeLock = ScopeLock.Acquire(Path.GetDirectoryName(lockPath)!);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var root = ScopeRoot(scope);
        var results = new List<ApplyResult>();
        using var transaction = TransactionSnapshot.Capture(
            itemList.Where(x => !ShouldSkip(x)), lockPath, root, scope,
            itemList.Where(x => !ShouldSkip(x)).Select(x => ManagedSnapshotPath(scope, x)));

        try
        {
            foreach (var item in itemList)
            {
                if (ShouldSkip(item))
                {
                    results.Add(new ApplyResult(item,
                        decisions.GetValueOrDefault(item) == DriftAction.Keep
                            ? ApplyOutcome.KeptLocalChanges
                            : ApplyOutcome.SkippedTransaction));
                    continue;
                }

                if (item.Existing?.Pinned == true && item.State == InstallState.Pinned)
                {
                    results.Add(new ApplyResult(item, ApplyOutcome.SkippedPinned));
                    continue;
                }

                if (item.State == InstallState.Installed)
                {
                    var current = lockFile.Find(item.Asset.Id, item.Provider, item.Asset.Kind);
                    if (current is not null)
                    {
                        current.Direct |= item.Direct;
                        current.RequiredBy = current.RequiredBy
                            .Concat(item.RequiredBy ?? [])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    results.Add(new ApplyResult(item, ApplyOutcome.AlreadyUpToDate));
                    continue;
                }

                var sourcePath = sources[item];
                var installedChecksum = item.State == InstallState.Adoptable || stagedAdoptions.Contains(item)
                    ? ContentHash.Compute(item.TargetPath)
                    : ApplyItem(item, sourcePath, root, scope, loaded, renderedAgents.GetValueOrDefault(item));
                var managedSnapshot = ManagedSnapshotPath(scope, item);
                DeleteExisting(managedSnapshot);
                ContentHash.CopyTree(item.TargetPath, managedSnapshot);

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
                    Pinned = item.Existing?.Pinned ?? false,
                    Direct = (item.Existing?.Direct ?? false) || item.Direct,
                    RequiredBy = (item.Existing?.RequiredBy ?? [])
                        .Concat(item.RequiredBy ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    RenderFingerprint = item.RenderFingerprint,
                    ManagedSnapshotPath = managedSnapshot
                });

                results.Add(new ApplyResult(item, item.Existing is null ? ApplyOutcome.Installed : ApplyOutcome.Updated));
            }

            JsonStore.Save(lockPath, lockFile);
            transaction.Commit();
        }
        catch (Exception ex)
        {
            transaction.Restore();
            if (ex is AgentPackException) throw;
            throw new AgentPackException(
                $"AgentPack rolled back the transaction after an apply failure: {ex.Message}",
                "No planned provider or lockfile changes were retained. Fix the cause and retry.",
                ExitCodes.Internal);
        }

        WriteScopeGitIgnore(scope);
        return results;

        bool ShouldSkip(InstallPlanItem item) =>
            decisions.GetValueOrDefault(item) == DriftAction.Keep || Owners(item).Any(skippedOwners.Contains);

        bool StagedCandidateMatches(InstallPlanItem item)
        {
            if (!File.Exists(item.TargetPath) && !Directory.Exists(item.TargetPath)) return false;
            return item.Target.Mode switch
            {
                InstallMode.CopyTree => ContentHash.Compute(sources[item]).Equals(
                    ContentHash.Compute(item.TargetPath), StringComparison.OrdinalIgnoreCase),
                InstallMode.RenderAgent => ContentHash.ComputeText(renderedAgents[item]).Equals(
                    ContentHash.Compute(item.TargetPath), StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        static IEnumerable<string> Owners(InstallPlanItem item)
        {
            if (item.Asset.Kind == AssetKind.Agents) yield return $"agent:{item.Asset.Id}";
            foreach (var owner in item.RequiredBy ?? []) yield return owner;
        }
    }

    public List<LockEntry> Remove(
        AssetKind? kind,
        IReadOnlyList<string> ids,
        IReadOnlyList<ProviderName>? providers,
        InstallScope scope,
        bool force = false,
        bool keepLocal = false)
    {
        var lockPath = _paths.GetLockPath(scope);
        using var scopeLock = ScopeLock.Acquire(Path.GetDirectoryName(lockPath)!);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var root = ScopeRoot(scope);

        var matches = lockFile.Entries.Where(entry =>
            (kind is null || entry.Kind == kind) &&
            (ids.Count == 0 || ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)) &&
            (providers is null or { Count: 0 } || providers.Contains(entry.Provider))).ToList();

        foreach (var entry in matches)
        {
            var installedPath = ResolveLockPath(entry.Path, root);
            var modified = entry.InstallMode != InstallMode.MergeMcp &&
                (File.Exists(installedPath) || Directory.Exists(installedPath)) &&
                !ContentHash.Compute(installedPath).Equals(entry.InstalledChecksum, StringComparison.OrdinalIgnoreCase);
            if (modified && !force && !keepLocal)
            {
                throw new AgentPackException(
                    $"'{entry.Id}' ({entry.Provider.Display()}) was modified locally.",
                    "Use --force to back up and delete it, or --keep-local to unregister it and leave the file unmanaged.",
                    ExitCodes.DriftOrConflict);
            }

            // Removing a direct dependency that is still owned by an agent only
            // changes its provenance; the dependency must remain installed.
            if (entry.Kind != AssetKind.Agents && entry.Direct && entry.RequiredBy.Count > 0)
            {
                entry.Direct = false;
                continue;
            }

            if (modified && keepLocal)
            {
                lockFile.Entries.Remove(entry);
                DeleteManagedSnapshot(entry);
                continue;
            }

            if (entry.InstallMode == InstallMode.MergeHook)
            {
                // The hook's executable content is always ours to delete.
                var supportPath = Path.GetFullPath(Path.Combine(root, HookMerger.SupportRelativePath(entry.Provider, entry.Id, scope)));
                if (Directory.Exists(supportPath))
                {
                    Backup(supportPath, scope);
                    Directory.Delete(supportPath, recursive: true);
                }
            }

            var isSharedConfig = entry.InstallMode == InstallMode.MergeMcp ||
                (entry.InstallMode == InstallMode.MergeHook && HookMerger.IsSharedConfigFile(entry.Provider));
            if (isSharedConfig)
            {
                // Shared provider config files are never deleted; only the lock entry goes.
                lockFile.Entries.Remove(entry);
                DeleteManagedSnapshot(entry);
                continue;
            }

            if (File.Exists(installedPath) || Directory.Exists(installedPath))
            {
                Backup(installedPath, scope);
                DeleteExisting(installedPath);
            }

            lockFile.Entries.Remove(entry);
            DeleteManagedSnapshot(entry);
        }

        foreach (var agent in matches.Where(x => x.Kind == AssetKind.Agents))
        {
            var owner = $"agent:{agent.Id}";
            foreach (var dependency in lockFile.Entries)
            {
                if (dependency.Provider != agent.Provider) continue;
                dependency.RequiredBy.RemoveAll(x => x.Equals(owner, StringComparison.OrdinalIgnoreCase));
            }
        }

        JsonStore.Save(lockPath, lockFile);
        return matches;
    }

    public PruneResult Prune(
        InstallScope scope,
        IReadOnlyList<ProviderName>? providers,
        bool apply,
        LoadedCatalog? loaded = null)
    {
        var lockPath = _paths.GetLockPath(scope);
        using var scopeLock = ScopeLock.Acquire(Path.GetDirectoryName(lockPath)!);
        var lockFile = JsonStore.Load<AgentPackLock>(lockPath);
        var root = ScopeRoot(scope);
        var orphans = lockFile.Entries.Where(x => !x.Direct && x.RequiredBy.Count == 0 &&
            (providers is null or { Count: 0 } || providers.Contains(x.Provider))).ToList();
        var clean = new List<LockEntry>();
        var modified = new List<LockEntry>();
        foreach (var entry in orphans)
        {
            var path = ResolveLockPath(entry.Path, root);
            var exists = File.Exists(path) || Directory.Exists(path);
            var currentChecksum = entry.InstallMode == InstallMode.MergeMcp && loaded is not null &&
                loaded.Catalog.Assets.FirstOrDefault(x => x.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) && x.Kind == AssetKind.Mcp) is { } mcp
                    ? McpMerger.CurrentChecksum(mcp, path,
                        new InstallTarget(entry.Provider, entry.Kind, entry.Path, entry.InstallMode, true), scope,
                        _resolver.TryResolve(loaded, mcp))
                    : exists ? ContentHash.Compute(path) : "";
            if (exists && !currentChecksum.Equals(entry.InstalledChecksum, StringComparison.OrdinalIgnoreCase))
                modified.Add(entry);
            else
                clean.Add(entry);
        }

        if (apply)
        {
            foreach (var entry in clean)
            {
                var path = ResolveLockPath(entry.Path, root);
                if (entry.InstallMode == InstallMode.MergeMcp && loaded is not null &&
                    loaded.Catalog.Assets.FirstOrDefault(x => x.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) && x.Kind == AssetKind.Mcp) is { } mcp)
                {
                    var target = new InstallTarget(entry.Provider, entry.Kind, entry.Path, entry.InstallMode, true);
                    McpMerger.Remove(mcp, path, target, scope, existing => Backup(existing, scope),
                        _resolver.TryResolve(loaded, mcp));
                }
                else if (entry.InstallMode is not (InstallMode.MergeMcp or InstallMode.MergeHook) &&
                    (File.Exists(path) || Directory.Exists(path)))
                {
                    Backup(path, scope);
                    DeleteExisting(path);
                }
                lockFile.Entries.Remove(entry);
                DeleteManagedSnapshot(entry);
            }
            JsonStore.Save(lockPath, lockFile);
        }
        return new PruneResult(clean, modified);
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
            var dependencyDriven = asset.Kind == AssetKind.Agents &&
                !_agentRenderer.Fingerprint(loaded, asset, entry.Provider, scope)
                    .Equals(entry.RenderFingerprint, StringComparison.OrdinalIgnoreCase);
            var sourceDriven = EffectiveSourceChecksum(loaded, asset, _resolver.TryResolve(loaded, asset)) is { } checksum &&
                !checksum.Equals(entry.SourceChecksum, StringComparison.OrdinalIgnoreCase);
            if (asset.Version.IsNewerThan(entry.Version) || dependencyDriven || sourceDriven)
            {
                var plan = Plan(loaded, [asset], [entry.Provider], scope,
                    entry.Direct ? null : entry.RequiredBy.FirstOrDefault());
                items.AddRange(plan.Items);
                skipped.AddRange(plan.Skipped);
            }
        }

        var distinct = items
            .GroupBy(x => (x.Asset.Id.ToLowerInvariant(), x.Asset.Kind, x.Provider))
            .Select(group =>
            {
                var first = group.First();
                return first with
                {
                    Direct = group.Any(x => x.Direct),
                    RequiredBy = group.SelectMany(x => x.RequiredBy ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };
            })
            .ToList();
        return new InstallPlan(distinct, skipped);
    }

    public void SetPinned(string id, bool pinned, InstallScope scope)
    {
        var lockPath = _paths.GetLockPath(scope);
        using var scopeLock = ScopeLock.Acquire(Path.GetDirectoryName(lockPath)!);
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

    private static InstallState DetermineState(
        Asset asset,
        string targetPath,
        InstallTarget target,
        LockEntry? existing,
        string root,
        InstallScope scope,
        string? renderFingerprint,
        string? sourceChecksum,
        string? sourcePath)
    {
        if (existing is null)
        {
            return File.Exists(targetPath) || Directory.Exists(targetPath)
                ? InstallState.UnmanagedPresent
                : InstallState.Available;
        }

        var installedPath = ResolveLockPath(existing.Path, root);
        if (!File.Exists(installedPath) && !Directory.Exists(installedPath)) return InstallState.Missing;

        var actual = target.Mode == InstallMode.MergeMcp
            ? McpMerger.CurrentChecksum(asset, installedPath, target, scope, sourcePath)
            : ContentHash.Compute(installedPath);
        if (!actual.Equals(existing.InstalledChecksum, StringComparison.OrdinalIgnoreCase)) return InstallState.LocalChanges;
        if (sourceChecksum is not null && !sourceChecksum.Equals(existing.SourceChecksum, StringComparison.OrdinalIgnoreCase))
            return existing.Pinned ? InstallState.Pinned : InstallState.UpdateAvailable;
        if (renderFingerprint is not null && !renderFingerprint.Equals(existing.RenderFingerprint, StringComparison.OrdinalIgnoreCase))
            return existing.Pinned ? InstallState.Pinned : InstallState.UpdateAvailable;
        if (asset.Version.IsNewerThan(existing.Version)) return existing.Pinned ? InstallState.Pinned : InstallState.UpdateAvailable;
        return InstallState.Installed;
    }

    private bool CandidateMatches(
        LoadedCatalog loaded,
        Asset asset,
        ProviderName provider,
        InstallScope scope,
        InstallTarget target,
        string? sourcePath,
        string targetPath)
    {
        try
        {
            if (target.Mode == InstallMode.CopyTree && sourcePath is not null)
                return ContentHash.Compute(sourcePath).Equals(ContentHash.Compute(targetPath), StringComparison.OrdinalIgnoreCase);
            if (target.Mode != InstallMode.RenderAgent || sourcePath is null) return false;
            var dependencies = new AgentDependencyResolver(loaded.Catalog).Resolve(asset, provider);
            if (dependencies.All.Any(x => _resolver.TryResolve(loaded, x) is null)) return false;
            var rendered = _agentRenderer.Render(loaded, asset, provider, scope);
            return ContentHash.ComputeText(rendered).Equals(ContentHash.Compute(targetPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string ResolveApplySource(LoadedCatalog loaded, InstallPlanItem item)
    {
        if (item.Target.Mode == InstallMode.RenderAgent)
        {
            return item.SourcePath ?? "";
        }

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

    private string ApplyItem(
        InstallPlanItem item,
        string sourcePath,
        string root,
        InstallScope scope,
        LoadedCatalog loaded,
        string? stagedAgent = null)
    {
        switch (item.Target.Mode)
        {
            case InstallMode.RenderAgent:
                var rendered = stagedAgent ?? _agentRenderer.Render(loaded, item.Asset, item.Provider, scope);
                if (File.Exists(item.TargetPath) &&
                    ContentHash.Compute(item.TargetPath).Equals(ContentHash.ComputeText(rendered), StringComparison.OrdinalIgnoreCase))
                {
                    return ContentHash.Compute(item.TargetPath);
                }
                if (File.Exists(item.TargetPath) || Directory.Exists(item.TargetPath)) Backup(item.TargetPath, scope);
                DeleteExisting(item.TargetPath);
                AtomicWrite.Text(item.TargetPath, rendered);
                return ContentHash.Compute(item.TargetPath);

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

    private static string? EffectiveSourceChecksum(LoadedCatalog loaded, Asset asset, string? sourcePath)
    {
        var declared = loaded.EffectiveChecksum(asset);
        if (declared is not null) return declared;
        return !string.IsNullOrWhiteSpace(sourcePath) && (File.Exists(sourcePath) || Directory.Exists(sourcePath))
            ? ContentHash.Compute(sourcePath)
            : null;
    }

    private void Backup(string path, InstallScope scope)
    {
        var root = scope == InstallScope.User ? _paths.Home : Path.Combine(_paths.WorkingDirectory, ".agentpack");
        var backupRoot = Path.Combine(root, "backups", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        var target = Path.Combine(backupRoot, ContentHash.ShortKey(Path.GetFullPath(path)),
            Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        ContentHash.CopyTree(path, target);
    }

    private string ManagedSnapshotPath(InstallScope scope, InstallPlanItem item) =>
        Path.Combine(scope == InstallScope.User ? _paths.Home : Path.Combine(_paths.WorkingDirectory, ".agentpack"),
            "managed", item.Provider.Display(), item.Asset.Kind.Display(), item.Asset.Id);

    private static void DeleteManagedSnapshot(LockEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ManagedSnapshotPath)) DeleteExisting(entry.ManagedSnapshotPath);
    }

    private void WriteScopeGitIgnore(InstallScope scope)
    {
        if (scope == InstallScope.User) return;
        var directory = Path.Combine(_paths.WorkingDirectory, ".agentpack");
        var gitIgnore = Path.Combine(directory, ".gitignore");
        Directory.CreateDirectory(directory);
        if (!File.Exists(gitIgnore))
        {
            File.WriteAllText(gitIgnore, "backups/\n.lock\n");
            return;
        }

        var lines = File.ReadAllLines(gitIgnore);
        if (!lines.Contains(".lock"))
        {
            File.AppendAllText(gitIgnore, ".lock\n");
        }
    }

    private static void DeleteExisting(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class TransactionSnapshot : IDisposable
    {
        private readonly string _directory;
        private readonly List<(string Original, string Backup, bool Existed)> _paths;
        private bool _finished;

        private TransactionSnapshot(string directory, List<(string Original, string Backup, bool Existed)> paths)
        {
            _directory = directory;
            _paths = paths;
        }

        public static TransactionSnapshot Capture(
            IEnumerable<InstallPlanItem> items,
            string lockPath,
            string root,
            InstallScope scope,
            IEnumerable<string> additionalPaths)
        {
            var directory = Path.Combine(Path.GetTempPath(), "agentpack-transaction-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var originals = items.Select(x => x.TargetPath).Append(lockPath).ToList();
            originals.AddRange(additionalPaths);
            originals.AddRange(items
                .Where(x => x.Target.Mode == InstallMode.MergeHook)
                .Select(x => Path.GetFullPath(Path.Combine(root, HookMerger.SupportRelativePath(x.Provider, x.Asset.Id, scope)))));
            var snapshots = new List<(string Original, string Backup, bool Existed)>();
            foreach (var original in originals.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var backup = Path.Combine(directory, ContentHash.ShortKey(original));
                var existed = File.Exists(original) || Directory.Exists(original);
                if (existed) ContentHash.CopyTree(original, backup);
                snapshots.Add((original, backup, existed));
            }
            return new TransactionSnapshot(directory, snapshots);
        }

        public void Commit() => _finished = true;

        public void Restore()
        {
            if (_finished) return;
            foreach (var (original, backup, existed) in _paths.AsEnumerable().Reverse())
            {
                DeleteExisting(original);
                if (existed) ContentHash.CopyTree(backup, original);
            }
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished) Restore();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(InstallPlanItem item) => System.IO.Path.Combine(
            Path, item.Provider.Display(), item.Asset.Kind.Display(), item.Asset.Id +
            (item.Provider == ProviderName.Codex ? ".toml" : ".md"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
