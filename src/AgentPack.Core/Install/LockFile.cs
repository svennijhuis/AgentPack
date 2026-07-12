namespace AgentPack.Core;

/// <summary>Per-scope record of what agentpack installed (project: .agentpack/lock.json, user: ~/.agentpack/lock.json).</summary>
public sealed class AgentPackLock
{
    public List<LockEntry> Entries { get; set; } = [];

    public LockEntry? Find(string id, ProviderName provider, AssetKind kind) =>
        Entries.FirstOrDefault(x =>
            x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
            x.Provider == provider &&
            x.Kind == kind);
}

public sealed class LockEntry
{
    public string Id { get; set; } = "";
    public AssetKind Kind { get; set; }
    public ProviderName Provider { get; set; }
    public string Version { get; set; } = "";
    public string Path { get; set; } = "";
    public InstallMode InstallMode { get; set; } = InstallMode.CopyTree;
    public string SourceChecksum { get; set; } = "";
    public string InstalledChecksum { get; set; } = "";

    /// <summary>
    /// For merge-mode installs: the exact fragment written into the shared provider
    /// config. Drift detection and removal work on this fragment, so other entries
    /// merged into the same file never register as local changes. Null for copy-tree
    /// installs and for entries written by agentpack &lt; 0.3.
    /// </summary>
    public string? Fragment { get; set; }

    public bool Pinned { get; set; }
}
