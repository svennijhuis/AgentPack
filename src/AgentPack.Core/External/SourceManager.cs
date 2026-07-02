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

    public void Sync(AgentPackSource source)
    {
        var target = SourceCachePath(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!Directory.Exists(Path.Combine(target, ".git")))
        {
            Ensure(ProcessRunner.Run("git", $"clone --branch {Escape(source.Branch)} {Escape(source.Url)} {Escape(target)}", _paths.WorkingDirectory),
                $"clone '{source.Name}' from {source.Url}");
            return;
        }

        Ensure(ProcessRunner.Run("git", "fetch --all --prune", target), $"fetch '{source.Name}'");
        Ensure(ProcessRunner.Run("git", $"checkout {Escape(source.Branch)}", target), $"checkout {source.Branch} for '{source.Name}'");
        Ensure(ProcessRunner.Run("git", "pull --ff-only", target), $"pull '{source.Name}'");
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
    private static string Escape(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
