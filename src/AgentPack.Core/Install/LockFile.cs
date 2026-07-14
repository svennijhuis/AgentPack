using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPack.Core;

/// <summary>Per-scope record of what agentpack installed (project: .agentpack/lock.json, user: ~/.agentpack/lock.json).</summary>
public sealed class AgentPackLock
{
    public List<LockEntry> Entries { get; set; } = [];

    /// <summary>Fields written by a newer agentpack survive a rewrite by this one.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

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
    /// Checksum of executable hook content stored beside a shared hook config.
    /// Null keeps lock files written by older AgentPack versions compatible.
    /// </summary>
    public string? SupportChecksum { get; set; }

    /// <summary>
    /// Exact merge-mode fragment written to a shared provider config. This lets
    /// upgrades and removal operate on AgentPack-owned content when catalog
    /// metadata is unavailable, while unrelated user entries remain untouched.
    /// </summary>
    public string? Fragment { get; set; }

    public bool Pinned { get; set; }
    public bool Direct { get; set; } = true;
    public List<string> RequiredBy { get; set; } = [];
    public string? RenderFingerprint { get; set; }
    public string? ManagedSnapshotPath { get; set; }

    /// <summary>Fields written by a newer agentpack survive a rewrite by this one.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
