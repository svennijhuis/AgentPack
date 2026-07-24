namespace AgentPack.Core;

public sealed class AgentPackPaths
{
    /// <summary>
    /// Test seam: relocates the platform default home so persistence tests never write
    /// to the developer's real profile. Never set in production.
    /// </summary>
#pragma warning disable CS0649 // Assigned only from the test assembly via InternalsVisibleTo.
    internal static string? DefaultHomeBaseOverride;
#pragma warning restore CS0649

    public AgentPackPaths(string? home = null, string? workingDirectory = null, string? providerHome = null)
    {
        // Precedence: explicit (tests) > AGENTPACK_HOME env > persisted choice ('config
        // --set-home') > platform default. The env var stays the highest-priority runtime
        // override so a shell can still redirect a single invocation.
        Home = home
            ?? EnvironmentHome
            ?? PersistedHome()
            ?? DefaultHome;
        WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();

        // The provider home is always the real user profile: AGENTPACK_HOME relocates
        // agentpack's own state, never where .claude/, .codex/, ... live.
        ProviderHome = providerHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string Home { get; }
    public string WorkingDirectory { get; }

    /// <summary>Root that user-scope provider paths (.claude/, .codex/, ...) are resolved against — the user's home.</summary>
    public string ProviderHome { get; }

    private static string? EnvironmentHome =>
        Environment.GetEnvironmentVariable("AGENTPACK_HOME") is { Length: > 0 } value ? value : null;

    /// <summary>True when AGENTPACK_HOME is set and therefore overrides any persisted home.</summary>
    public static bool HomeSetByEnvironment => EnvironmentHome is not null;

    private static string DefaultHomeBase =>
        DefaultHomeBaseOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where agentpack keeps its state unless the environment or a persisted choice overrides it.</summary>
    public static string DefaultHome => Path.Combine(DefaultHomeBase, ".agentpack");

    /// <summary>
    /// Pointer that records a home relocation. It lives at the fixed default location —
    /// never inside the (possibly moved) home — so it can be found again after the move.
    /// </summary>
    private static string HomePointerPath => Path.Combine(DefaultHome, "home.path");

    /// <summary>The persisted home override, or null when none is set (or the pointer is blank/unreadable).</summary>
    public static string? PersistedHome()
    {
        try
        {
            if (!File.Exists(HomePointerPath)) return null;
            var value = File.ReadAllText(HomePointerPath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Persists a home directory so later invocations use it without AGENTPACK_HOME set.</summary>
    public static void PersistHome(string path)
    {
        Directory.CreateDirectory(DefaultHome);
        File.WriteAllText(HomePointerPath, Path.GetFullPath(path));
    }

    /// <summary>Clears any persisted home, reverting to the platform default.</summary>
    public static void ClearPersistedHome()
    {
        if (File.Exists(HomePointerPath)) File.Delete(HomePointerPath);
    }

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
