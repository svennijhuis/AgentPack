namespace AgentPack.Core;

public sealed class AgentPackPaths
{
    public AgentPackPaths(string? home = null, string? workingDirectory = null, string? providerHome = null)
    {
        Home = home
            ?? Environment.GetEnvironmentVariable("AGENTPACK_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentpack");
        WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();

        // The provider home is always the real user profile: AGENTPACK_HOME relocates
        // agentpack's own state, never where .claude/, .codex/, ... live.
        ProviderHome = providerHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string Home { get; }
    public string WorkingDirectory { get; }

    /// <summary>Root that user-scope provider paths (.claude/, .codex/, ...) are resolved against — the user's home.</summary>
    public string ProviderHome { get; }

    public string ConfigPath => Path.Combine(Home, "config.json");
    public string CacheRoot => Path.Combine(Home, "cache");
    public string ExternalCacheRoot => Path.Combine(CacheRoot, "external");
    public string SubmissionsRoot => Path.Combine(Home, "submissions");
    public string ProjectLockPath => Path.Combine(WorkingDirectory, ".agentpack", "lock.json");
    public string UserLockPath => Path.Combine(Home, "lock.json");

    public string GetLockPath(InstallScope scope) => scope == InstallScope.User ? UserLockPath : ProjectLockPath;
}

public enum InstallScope
{
    Project,
    User
}
