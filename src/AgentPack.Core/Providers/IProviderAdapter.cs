namespace AgentPack.Core;

public enum InstallMode
{
    /// <summary>Copy the content tree to the target path.</summary>
    CopyTree,

    /// <summary>Merge an MCP server entry into the provider's config file.</summary>
    MergeMcp,

    /// <summary>Copy hook content and register it in the provider's hook config file.</summary>
    MergeHook,

    /// <summary>Translate a single content file into the provider's own format and write it to the target path.</summary>
    ConvertFile
}

public static class InstallModes
{
    /// <summary>
    /// Modes whose target file is wholly owned by agentpack: drift is a whole-file
    /// checksum, no fragment is recorded, and removal deletes the file. Merge modes
    /// share the target file with user content and track only their fragment.
    /// </summary>
    public static bool OwnsWholeTarget(this InstallMode mode) =>
        mode is InstallMode.CopyTree or InstallMode.ConvertFile;
}

public sealed record InstallTarget(
    ProviderName Provider,
    AssetKind Kind,
    string RelativePath,
    InstallMode Mode,
    bool IsFileTarget = false);

/// <summary>
/// What a provider does with an asset: either a concrete install target,
/// or an explicit "this product has no such concept" — never an invented path.
/// </summary>
public abstract record ProviderPlan
{
    private ProviderPlan() { }

    public sealed record Supported(InstallTarget Target) : ProviderPlan;

    public sealed record Unsupported(string Reason) : ProviderPlan;
}

public interface IProviderAdapter
{
    ProviderName Name { get; }

    /// <summary>True when the working directory shows signs of this provider being used.</summary>
    bool Detect(string root);

    ProviderPlan Plan(Asset asset, bool userScope);
}

public static class ProviderRegistry
{
    public static readonly IReadOnlyList<IProviderAdapter> All =
    [
        new ClaudeAdapter(),
        new CodexAdapter(),
        new CopilotAdapter(),
        new CursorAdapter()
    ];

    public static IProviderAdapter Get(ProviderName provider) => All.First(x => x.Name == provider);

    public static IReadOnlyList<ProviderName> Detect(string root) =>
        All.Where(x => x.Detect(root)).Select(x => x.Name).ToList();
}

internal static class AdapterHelpers
{
    public static bool Exists(string root, params string[] parts) =>
        File.Exists(Path.Combine([root, .. parts])) || Directory.Exists(Path.Combine([root, .. parts]));

    public static ProviderPlan Supported(ProviderName provider, Asset asset, string relativePath, InstallMode mode, bool isFileTarget = false) =>
        new ProviderPlan.Supported(new InstallTarget(provider, asset.Kind, relativePath, mode, isFileTarget));

    public static ProviderPlan Unsupported(string reason) => new ProviderPlan.Unsupported(reason);
}
