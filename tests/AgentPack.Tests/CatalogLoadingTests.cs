using AgentPack.Core;

namespace AgentPack.Tests;

public class CatalogLoadingTests
{
    [Fact]
    public void InfersIdKindAndContentPathFromFolderLayout()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "grill-me", """
            name: Grill Me
            version: 1.0.0
            description: Test.
            """);

        var loaded = Load(temp);
        var asset = Assert.Single(loaded.Catalog.Assets);
        Assert.Equal("grill-me", asset.Id);
        Assert.Equal(AssetKind.Skills, asset.Kind);
        var local = Assert.IsType<AssetSource.Local>(asset.Source);
        Assert.Equal("assets/skills/grill-me/content", local.RelativePath);
    }

    [Fact]
    public void OmittedProvidersMeansAllProviders()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "everywhere", "name: Everywhere\nversion: 1.0.0\n");
        WriteAsset(root, "skills", "limited", "name: Limited\nversion: 1.0.0\nproviders: [codex, claude]\n");

        var loaded = Load(temp);
        Assert.Equal(ProviderNames.All, loaded.Catalog.Assets.First(x => x.Id == "everywhere").Providers);
        Assert.Equal([ProviderName.Codex, ProviderName.Claude], loaded.Catalog.Assets.First(x => x.Id == "limited").Providers);
    }

    [Fact]
    public void SourceShorthandParsesUrlAndRef()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "pdf-review",
            "name: PDF Review\nversion: 1.0.0\nsource: https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d\n");

        var loaded = Load(temp);
        var external = Assert.IsType<AssetSource.External>(loaded.Catalog.Assets.Single().Source);
        Assert.Equal("https://github.com/anthropics/skills/tree/main/skills/pdf", external.Url);
        Assert.Equal("9d2f1ae187231d8199c64b5b762e1bdf2244733d", external.Ref);
    }

    [Fact]
    public void ExternalSourceParsesLicense()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "pdf-review", """
            name: PDF Review
            version: 1.0.0
            source:
              url: https://github.com/anthropics/skills/tree/main/skills/pdf
              ref: 9d2f1ae187231d8199c64b5b762e1bdf2244733d
              license: Apache-2.0
            """);

        var external = Assert.IsType<AssetSource.External>(Load(temp).Catalog.Assets.Single().Source);
        Assert.Equal("Apache-2.0", external.License);
    }

    [Fact]
    public void ExternalSourceWithoutRefIsAnError()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "floaty",
            "name: Floaty\nversion: 1.0.0\nsource:\n  url: https://github.com/example/skills\n");

        var ex = Assert.Throws<AgentPackException>(() => Load(temp));
        Assert.Contains("ref", ex.Message);
    }

    [Fact]
    public void InvalidVersionAndKindReportAllErrorsAtOnce()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "widgets", "bad-one", "name: Bad\nversion: not-semver\n");

        var ex = Assert.Throws<AgentPackException>(() => Load(temp));
        Assert.Contains("kind", ex.Message);
        Assert.Contains("not-semver", ex.Message);
    }

    [Fact]
    public void RemovedBundleFieldsAreRejectedInsteadOfSilentlyMigrated()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.2.0
            bundles:
              - id: old-bundle
                assets: []
            """);

        var ex = Assert.Throws<AgentPackException>(() => Load(temp));
        Assert.Contains("bundles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovedProfileBundleFieldIsRejected()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.2.0
            profiles:
              - id: backend
                name: Backend
                bundles: [old-bundle]
                assets: []
            """);

        var ex = Assert.Throws<AgentPackException>(() => Load(temp));
        Assert.Contains("bundles", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A catalog written by a newer agentpack must still load, otherwise
    /// minimumAgentPackVersion could never report the upgrade and one contributor's
    /// stray manifest key would break every command for every user.
    /// </summary>
    [Fact]
    public void UnknownForwardCompatibleFieldsAreIgnoredRatherThanFatal()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.2.0
            registries:
              - id: future-feature
            """);
        WriteAsset(root, "skills", "future-skill",
            "name: Future Skill\nversion: 1.0.0\nmaintainer: someone@example.com\nreviewBoard: [a, b]\n");

        var loaded = Load(temp);

        var asset = Assert.Single(loaded.Catalog.Assets);
        Assert.Equal("future-skill", asset.Id);
    }

    [Fact]
    public void MinimumAgentPackVersionSurvivesUnknownFieldsFromANewerRelease()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.2.0
            minimumAgentPackVersion: 99.0.0
            registries: []
            """);

        var loaded = Load(temp);

        Assert.Equal("99.0.0", loaded.Catalog.MinimumAgentPackVersion?.ToString());
    }

    [Fact]
    public void ChecksumComesFromCatalogLockWhenManifestOmitsIt()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "hashed", "name: Hashed\nversion: 1.0.0\n");

        var paths = TestData.Paths(temp);
        var loadedBefore = new CatalogLayerLoader(new SourceManager(paths)).Load();
        Assert.Null(loadedBefore.EffectiveChecksum(loadedBefore.Catalog.Assets.Single()));

        var result = new CatalogLockWriter(paths).Generate(loadedBefore, fetchExternal: false);
        result.Lock.Save(CatalogLockFile.PathFor(loadedBefore.PrimaryCatalogPath));

        var loadedAfter = new CatalogLayerLoader(new SourceManager(paths)).Load();
        var checksum = loadedAfter.EffectiveChecksum(loadedAfter.Catalog.Assets.Single());
        Assert.NotNull(checksum);
        Assert.StartsWith("sha256:", checksum);
    }

    [Fact]
    public void RegisteredSourceAutoSyncsOnFirstUse()
    {
        using var temp = new TempDir();

        // A local git repo acts as the org catalog.
        var catalogRepo = Path.Combine(temp.Path, "org-catalog");
        WriteMinimalCatalog(catalogRepo);
        WriteAsset(catalogRepo, "skills", "org-skill", "name: Org Skill\nversion: 1.0.0\n");
        Run("git", ["init", "-q", "-b", "main"], catalogRepo);
        Run("git", ["add", "-A"], catalogRepo);
        Run("git", ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init"], catalogRepo);

        // A consuming repo with no catalog.yaml and no prior catalog sync.
        var paths = TestData.Paths(temp, "consumer");
        var sources = new SourceManager(paths);
        sources.UseSource("org", catalogRepo);

        var loaded = new CatalogLayerLoader(sources).Load();
        Assert.Contains(loaded.Catalog.Assets, x => x.Id == "org-skill");
    }

    private static void Run(string file, string[] args, string cwd)
    {
        var result = AgentPack.Core.ProcessRunner.Run(file, args, cwd);
        Assert.Equal(0, result.ExitCode);
    }

    private static void WriteMinimalCatalog(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), "schemaVersion: \"1\"\ncatalogVersion: 0.1.0\n");
    }

    [Fact]
    public void MalformedCatalogLockYamlFailsWithActionableError()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "catalog.lock.yaml");
        File.WriteAllText(path, "entries: [ {\n");

        var ex = Assert.Throws<AgentPackException>(() => CatalogLockFile.Load(path));
        Assert.Contains("catalog.lock.yaml", ex.Message);
        Assert.Contains("agentpack catalog lock", ex.Hint);
    }

    private static void WriteAsset(string root, string kind, string id, string manifestYaml)
    {
        var dir = Path.Combine(root, "assets", kind, id);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "FILE.md"), $"# {id}\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"), manifestYaml);
    }

    private static LoadedCatalog Load(TempDir temp)
    {
        var paths = TestData.Paths(temp);
        return new CatalogLayerLoader(new SourceManager(paths)).Load();
    }
}
