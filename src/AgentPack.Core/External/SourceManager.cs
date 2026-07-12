namespace AgentPack.Core;

public sealed class AgentPackConfig
{
    public List<AgentPackSource> Sources { get; set; } = [];
}

public sealed class AgentPackSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Branch { get; set; } = "main";
}

/// <summary>Manages registered catalog source repos and their local clones.</summary>
public sealed class SourceManager
{
    private readonly AgentPackPaths _paths;

    public SourceManager(AgentPackPaths paths)
    {
        _paths = paths;
    }

    public AgentPackConfig LoadConfig() => JsonStore.Load<AgentPackConfig>(_paths.ConfigPath);

    public void AddSource(string name, string url, string branch = "main")
    {
        var config = LoadConfig();
        var existing = config.Sources.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            config.Sources.Add(new AgentPackSource { Name = name, Url = url, Branch = branch });
        }
        else
        {
            existing.Url = url;
            existing.Branch = branch;
        }

        JsonStore.Save(_paths.ConfigPath, config);
    }

    public string SourceCachePath(AgentPackSource source) => Path.Combine(_paths.CacheRoot, "sources", Sanitize(source.Name));

    /// <summary>How old a synced catalog may get before commands try to refresh it.</summary>
    public static readonly TimeSpan MaxCatalogAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Re-syncs the source that <paramref name="catalogPath"/> came from when the local
    /// clone is older than <see cref="MaxCatalogAge"/>. Never fails the command: offline
    /// (or any sync error) falls back to the cached copy with a warning to show the user.
    /// </summary>
    public CatalogIssue? RefreshIfStale(string catalogPath)
    {
        var source = LoadConfig().Sources.FirstOrDefault(x =>
            Path.GetFullPath(catalogPath).StartsWith(Path.GetFullPath(SourceCachePath(x)) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (source is null) return null;

        var marker = SyncMarkerPath(source);
        var lastSync = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
        if (DateTime.UtcNow - lastSync < MaxCatalogAge) return null;

        try
        {
            Sync(source);
            return null;
        }
        catch (AgentPackException ex)
        {
            var age = lastSync == DateTime.MinValue ? "an unknown time" : $"{(DateTime.UtcNow - lastSync).TotalDays:0.#} day(s)";
            return new CatalogIssue(IssueSeverity.Warning, "catalog.stale",
                $"Could not refresh catalog source '{source.Name}' ({FirstLine(ex.Message)}); using the cached copy from {age} ago.");
        }
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
        if (!Directory.Exists(Path.Combine(target, ".git")))
        {
            Ensure(ProcessRunner.Run("git", ["clone", "--branch", branch, "--", url, target], _paths.WorkingDirectory),
                $"clone '{source.Name}' from {source.Url}");
            MarkSynced(source);
            return;
        }

        Ensure(ProcessRunner.Run("git", ["fetch", "--all", "--prune"], target), $"fetch '{source.Name}'");
        Ensure(ProcessRunner.Run("git", ["checkout", branch, "--"], target), $"checkout {source.Branch} for '{source.Name}'");
        Ensure(ProcessRunner.Run("git", ["pull", "--ff-only"], target), $"pull '{source.Name}'");
        MarkSynced(source);
    }

    /// <summary>
    /// Finds the catalog, zero-config where possible:
    /// 1. explicit path; 2. catalog.yaml in the working directory (the catalog repo itself);
    /// 3. the first registered source (auto-synced on first use);
    /// 4. the AGENTPACK_CATALOG_URL environment variable (auto-registered and synced),
    ///    so organizations can bake the catalog in and developers never run 'source add'.
    /// </summary>
    public string ResolveCatalogPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);

        var local = Path.Combine(_paths.WorkingDirectory, "catalog.yaml");
        if (File.Exists(local)) return local;

        var config = LoadConfig();
        var first = config.Sources.FirstOrDefault();

        if (first is null)
        {
            var envUrl = Environment.GetEnvironmentVariable("AGENTPACK_CATALOG_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
            {
                var branch = Environment.GetEnvironmentVariable("AGENTPACK_CATALOG_BRANCH") ?? "main";
                AddSource("org", envUrl, branch);
                first = LoadConfig().Sources.First();
            }
        }

        if (first is null)
        {
            throw new AgentPackException(
                "No catalog found.",
                "Run from a catalog repo, register one with 'agentpack source add <name> <git-url>', " +
                "or set AGENTPACK_CATALOG_URL to your catalog's git URL.");
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

    private static void Ensure(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new AgentPackException(
                $"git failed to {operation}: {FirstLine(result.Error)}",
                "Check the URL, branch, and your git credentials.");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown error";

    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
