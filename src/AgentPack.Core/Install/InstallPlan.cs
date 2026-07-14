using AgentPack.Core.Primitives;

namespace AgentPack.Core;

public enum InstallState
{
    /// <summary>Not installed yet.</summary>
    Available,

    /// <summary>Installed and matching the lockfile checksum.</summary>
    Installed,

    /// <summary>Installed, and the catalog has a newer version.</summary>
    UpdateAvailable,

    /// <summary>An update exists but the entry is pinned.</summary>
    Pinned,

    /// <summary>The installed content was modified after install — needs a drift decision.</summary>
    LocalChanges,

    /// <summary>The lockfile references content that no longer exists on disk.</summary>
    Missing,

    /// <summary>Something already exists at the target path that agentpack did not install.</summary>
    UnmanagedPresent,

    /// <summary>An unmanaged target is byte-for-byte identical and can be adopted without rewriting.</summary>
    Adoptable
}

public static class InstallStates
{
    public static string Display(this InstallState state) => state switch
    {
        InstallState.Available => "install",
        InstallState.Installed => "up to date",
        InstallState.UpdateAvailable => "update",
        InstallState.Pinned => "pinned",
        InstallState.LocalChanges => "local changes",
        InstallState.Missing => "missing",
        InstallState.UnmanagedPresent => "unmanaged file present",
        InstallState.Adoptable => "adopt identical file",
        _ => state.ToString()
    };
}

public sealed record InstallPlanItem(
    Asset Asset,
    ProviderName Provider,
    string? SourcePath,
    string TargetPath,
    InstallTarget Target,
    InstallState State,
    LockEntry? Existing,
    bool Direct = true,
    IReadOnlyList<string>? RequiredBy = null,
    string? RenderFingerprint = null,
    string? StagedCandidatePath = null);

/// <summary>An asset × provider combination that cannot be installed, with the honest reason.</summary>
public sealed record SkippedInstall(Asset Asset, ProviderName Provider, string Reason);

public sealed record InstallPlan(IReadOnlyList<InstallPlanItem> Items, IReadOnlyList<SkippedInstall> Skipped);

public enum DriftAction
{
    Overwrite,
    Keep
}

public enum ApplyOutcome
{
    Installed,
    Updated,
    KeptLocalChanges,
    SkippedTransaction,
    SkippedPinned,
    AlreadyUpToDate
}

public sealed record ApplyResult(InstallPlanItem Item, ApplyOutcome Outcome);

public sealed record PruneResult(IReadOnlyList<LockEntry> Clean, IReadOnlyList<LockEntry> Modified);

public static class SemVersionExtensions
{
    public static bool IsNewerThan(this SemVersion version, string lockVersion) =>
        !SemVersion.TryParse(lockVersion, out var installed) || version.CompareTo(installed) > 0;
}
