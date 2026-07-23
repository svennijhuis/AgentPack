namespace AgentPack.Core;

public sealed class AgentPackConfig
{
    public AgentPackSource? Catalog { get; set; }

    /// <summary>Fields written by a newer agentpack survive a rewrite by this one.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

public sealed class AgentPackSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Branch { get; set; } = "main";

    /// <summary>Fields written by a newer agentpack survive a rewrite by this one.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

/// <summary>Manages the single active catalog repository and its local clone.</summary>
public sealed class SourceManager
{
    private static readonly GitRunner Git = new("Check the URL, branch, and your git credentials.");

    private readonly AgentPackPaths _paths;
    private readonly AgentPackSource? _defaultSource;
    private AgentPackConfig? _config;

    public SourceManager(AgentPackPaths paths, AgentPackSource? defaultSource = null)
    {
        _paths = paths;
        _defaultSource = defaultSource;
    }

    /// <summary>
    /// Cached for the manager's lifetime: a single command asks for the active catalog
    /// several times (resolve, refresh, describe) and config.json cannot change under it
    /// mid-command. <see cref="UseSource"/> refreshes the cache when it writes.
    /// </summary>
    public AgentPackConfig LoadConfig() => _config ??= JsonStore.Load<AgentPackConfig>(_paths.ConfigPath);

    /// <summary>Validates the coordinates of a catalog without selecting it yet.</summary>
    public static AgentPackSource DescribeSource(string name, string url, string branch = "main") =>
        new()
        {
            Name = ProcessRunner.SafeGitArg(name, "catalog name"),
            Url = ProcessRunner.SafeGitArg(url, "repository URL"),
            Branch = ProcessRunner.SafeGitArg(branch, "branch name")
        };

    /// <summary>
    /// Sets the single active base catalog used by consumer commands. Callers should sync
    /// first so a URL that cannot be reached never becomes the stored selection.
    /// </summary>
    public AgentPackSource UseSource(string name, string url, string branch = "main")
    {
        var source = DescribeSource(name, url, branch);
        var config = LoadConfig();
        config.Catalog = source;
        JsonStore.Save(_paths.ConfigPath, config);
        _config = config;
        return source;
    }

    /// <summary>The active configured, environment-provided, or built-in catalog.</summary>
    public AgentPackSource? EffectiveSource()
    {
        var configured = LoadConfig().Catalog;
        if (configured is not null) return configured;

        var environmentUrl = Environment.GetEnvironmentVariable("AGENTPACK_CATALOG_URL");
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            return new AgentPackSource
            {
                Name = "organization",
                Url = environmentUrl,
                Branch = Environment.GetEnvironmentVariable("AGENTPACK_CATALOG_BRANCH") ?? "main"
            };
        }

        return _defaultSource;
    }

    /// <summary>
    /// The active catalog for a command that cannot proceed without one. Only reachable
    /// when the built-in catalog is disabled, so every caller says the same thing.
    /// </summary>
    public AgentPackSource RequireEffectiveSource() =>
        EffectiveSource() ?? throw new AgentPackException(
            "No catalog is configured.",
            "Select one with 'agentpack catalog use <git-url>'.");

    public string SourceCachePath(AgentPackSource source) => Path.Combine(_paths.CacheRoot, "sources", Sanitize(source.Name));

    /// <summary>How old a synced catalog may get before commands try to refresh it.</summary>
    public static readonly TimeSpan MaxCatalogAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan ImmediateRefreshWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Re-syncs the source that <paramref name="catalogPath"/> came from when the local
    /// clone is older than <see cref="MaxCatalogAge"/>. Never fails the command: offline
    /// (or any sync error) falls back to the cached copy with a warning to show the user.
    /// </summary>
    public CatalogIssue? RefreshIfStale(string catalogPath, bool force = false)
    {
        var source = SourceOwning(catalogPath);
        if (source is null) return null;

        var marker = SyncMarkerPath(source);
        var lastSync = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
        if (DateTime.UtcNow - lastSync < (force ? ImmediateRefreshWindow : MaxCatalogAge)) return null;

        try
        {
            Sync(source);
            return null;
        }
        catch (AgentPackException ex)
        {
            var ageText = lastSync == DateTime.MinValue ? "an unknown time" : $"{(DateTime.UtcNow - lastSync).TotalDays:0.#} day(s)";
            return new CatalogIssue(IssueSeverity.Warning, "catalog.stale",
                $"Could not refresh catalog source '{source.Name}' ({ProcessRunner.FirstLine(ex.Message)}); using the cached copy from {ageText} ago.");
        }
    }

    public DateTime? LastSyncedAt(AgentPackSource source)
    {
        var marker = SyncMarkerPath(source);
        return File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : null;
    }

    public string DescribeCatalog(string catalogPath)
    {
        var source = SourceOwning(catalogPath);
        if (source is null) return $"local ({Path.GetFullPath(catalogPath)})";

        var revision = HeadRevision(SourceCachePath(source));
        return $"{source.Name} ({source.Url}, {source.Branch}{(revision is null ? "" : $" @ {revision}")})";
    }

    /// <summary>Short HEAD of a git working tree, or null when it is not readable.</summary>
    public static string? HeadRevision(string directory)
    {
        var head = ProcessRunner.Run("git", ["rev-parse", "--short", "HEAD"], directory);
        return head.ExitCode == 0 ? head.Output.Trim() : null;
    }

    /// <summary>The active source whose local clone contains <paramref name="catalogPath"/>, if any.</summary>
    private AgentPackSource? SourceOwning(string catalogPath)
    {
        if (EffectiveSource() is not { } source) return null;
        // Windows paths compare case-insensitively; Ordinal would silently skip the refresh.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var cachePrefix = Path.GetFullPath(SourceCachePath(source)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(catalogPath).StartsWith(cachePrefix, comparison) ? source : null;
    }

    private void MarkSynced(AgentPackSource source)
    {
        File.WriteAllText(SyncMarkerPath(source), DateTimeOffset.UtcNow.ToString("O"));
    }

    private string SyncMarkerPath(AgentPackSource source) => SourceCachePath(source) + ".synced";

    public void Sync(AgentPackSource source)
    {
        var target = SourceCachePath(source);
        var branch = ProcessRunner.SafeGitArg(source.Branch, "branch name");
        var url = ProcessRunner.SafeGitArg(source.Url, "repository URL");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // Concurrent agentpack processes syncing the same source would run git
        // against the same clone; serialize on the sources directory. The lock
        // cannot live inside the clone dir — git clone needs it empty.
        using var syncLock = ScopeLock.Acquire(Path.GetDirectoryName(target)!);
        if (!Directory.Exists(Path.Combine(target, ".git")))
        {
            // Blobless: the cache only ever reads the checked-out tree, so historical file
            // contents are dead weight — a catalog that lives beside application source
            // would otherwise be downloaded in full. Servers without filter support
            // fall back to a normal clone on their own.
            Git.Run(["clone", "--filter=blob:none", "--branch", branch, "--", url, target], _paths.WorkingDirectory,
                $"git failed to clone '{source.Name}' from {source.Url}");
            MarkSynced(source);
            return;
        }

        // A catalog name may be reused for a different URL through `catalog use`.
        // Keep the cache identity stable, but make sure it follows the selected repo.
        Git.Run(["remote", "set-url", "origin", url], target, $"git failed to select repository for '{source.Name}'");
        Git.Run(["fetch", "--all", "--prune"], target, $"git failed to fetch '{source.Name}'");
        Git.Run(["checkout", branch, "--"], target, $"git failed to checkout {source.Branch} for '{source.Name}'");
        Git.Run(["pull", "--ff-only"], target, $"git failed to pull '{source.Name}'");
        MarkSynced(source);
    }

    /// <summary>
    /// Finds the catalog, zero-config where possible:
    /// 1. explicit path; 2. catalog.yaml in the working directory (the catalog repo itself);
    /// 3. the selected catalog (auto-synced on first use);
    /// 4. the AGENTPACK_CATALOG_URL organization override;
    /// 5. the built-in official catalog;
    /// A project .agentpack directory stores install state, never another catalog.
    /// </summary>
    public string ResolveCatalogPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);

        var local = Path.Combine(_paths.WorkingDirectory, "catalog.yaml");
        if (File.Exists(local)) return local;

        var first = EffectiveSource();

        if (first is null)
        {
            throw new AgentPackException(
                "No catalog found.",
                "Run from a catalog repo, select one with 'agentpack catalog use <git-url>', or set AGENTPACK_CATALOG_URL.");
        }

        var cached = Path.Combine(SourceCachePath(first), "catalog.yaml");
        if (!File.Exists(cached))
        {
            // First use: fetch instead of telling the user to run another command.
            Sync(first);
            if (!File.Exists(cached))
            {
                throw new AgentPackException(
                    $"Catalog source '{first.Name}' has no catalog.yaml at its root.",
                    "Check the repository URL and branch.");
            }
        }

        return cached;
    }

    private static string Sanitize(string value)
    {
        // Distinct names like "a/b" and "a-b" must not collapse to the same cache dir.
        var safe = string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        return safe == value ? safe : safe + "-" + ContentHash.ShortKey(value);
    }
}
