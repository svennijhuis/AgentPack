using AgentPack.Core;
using AgentPack.Core.Primitives;

namespace AgentPack.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentpack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}

public static class TestData
{
    public static Asset Asset(
        AssetKind kind,
        string id,
        string version = "1.0.0",
        AssetSource? source = null,
        IReadOnlyList<ProviderName>? providers = null,
        McpServer? mcp = null,
        HookSpec? hook = null,
        AssetStatus status = AssetStatus.Recommended)
    {
        return new Asset
        {
            Id = id,
            Name = id,
            Kind = kind,
            Version = SemVersion.Parse(version),
            Providers = providers ?? ProviderNames.All,
            Source = source ?? new AssetSource.Local($"assets/{kind.Display()}/{id}/content", null),
            Mcp = mcp,
            Hook = hook,
            Status = status
        };
    }

    /// <summary>A LoadedCatalog wrapper for directly-constructed assets rooted at <paramref name="root"/>.</summary>
    public static LoadedCatalog Loaded(string root, params Asset[] assets)
    {
        var catalog = new Catalog { Assets = assets };
        return new LoadedCatalog(catalog, System.IO.Path.Combine(root, "catalog.yaml"), [root], new CatalogLockFile(), []);
    }

    /// <summary>Writes a local asset's content folder and returns the asset pointing at it.</summary>
    public static Asset WriteLocalAsset(string root, AssetKind kind, string id, string version = "1.0.0",
        IReadOnlyDictionary<string, string>? files = null, McpServer? mcp = null, HookSpec? hook = null)
    {
        var contentRelative = $"assets/{kind.Display()}/{id}/content";
        var contentPath = System.IO.Path.Combine(root, contentRelative);
        Directory.CreateDirectory(contentPath);
        foreach (var (name, body) in files ?? DefaultFiles(kind, id))
        {
            var filePath = System.IO.Path.Combine(contentPath, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, body);
        }

        return Asset(kind, id, version, new AssetSource.Local(contentRelative, null), mcp: mcp, hook: hook);
    }

    private static Dictionary<string, string> DefaultFiles(AssetKind kind, string id) => kind switch
    {
        AssetKind.Hooks => new Dictionary<string, string> { ["hook.sh"] = "#!/usr/bin/env bash\necho ok\n" },
        AssetKind.Mcp => new Dictionary<string, string> { ["mcp.json"] = """{"name":"NAME","transport":"stdio","command":"run-server","envVars":["TOKEN"]}""".Replace("NAME", id) },
        _ => new Dictionary<string, string> { ["SKILL.md"] = $"# {id}\n" }
    };

    public static AgentPackPaths Paths(TempDir dir, string? workSubdir = null)
    {
        var work = workSubdir is null ? System.IO.Path.Combine(dir.Path, "work") : System.IO.Path.Combine(dir.Path, workSubdir);
        Directory.CreateDirectory(work);
        var home = System.IO.Path.Combine(dir.Path, "home", ".agentpack");
        Directory.CreateDirectory(home);
        return new AgentPackPaths(home, work);
    }
}
