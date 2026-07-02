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
    public void BundlesAreMigratedIntoProfilesWithWarning()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.1.0
            bundles:
              - id: backend-defaults
                name: Backend Defaults
                assets: [grill-me]
            profiles:
              - id: backend
                name: Backend
                bundles: [backend-defaults]
                assets: []
            """);
        WriteAsset(root, "skills", "grill-me", "name: Grill Me\nversion: 1.0.0\n");

        var loaded = Load(temp);
        var profile = Assert.Single(loaded.Catalog.Profiles);
        Assert.Contains("grill-me", profile.Assets);
        Assert.Contains(loaded.Warnings, w => w.Code == "catalog.bundles.removed");
    }

    [Fact]
    public void ProjectOverlayAddsTeamAssets()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "org-skill", "name: Org Skill\nversion: 1.0.0\n");

        var overlayRoot = Path.Combine(root, ".agentpack");
        Directory.CreateDirectory(overlayRoot);
        File.WriteAllText(Path.Combine(overlayRoot, "catalog.yaml"), "schemaVersion: \"1\"\n");
        WriteAsset(overlayRoot, "prompts", "team-prompt", "name: Team Prompt\nversion: 1.0.0\n");

        var loaded = Load(temp);
        Assert.Equal(2, loaded.Catalog.Assets.Count);
        Assert.Contains(loaded.Catalog.Assets, x => x.Id == "team-prompt" && x.Kind == AssetKind.Prompts);
    }

    [Fact]
    public void ChecksumComesFromCatalogLockWhenManifestOmitsIt()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "work");
        WriteMinimalCatalog(root);
        WriteAsset(root, "skills", "hashed", "name: Hashed\nversion: 1.0.0\n");

        var paths = TestData.Paths(temp);
        var loadedBefore = new CatalogLayerLoader(new SourceManager(paths), paths).Load();
        Assert.Null(loadedBefore.EffectiveChecksum(loadedBefore.Catalog.Assets.Single()));

        var result = new CatalogLockWriter(paths).Generate(loadedBefore, fetchExternal: false);
        result.Lock.Save(CatalogLockFile.PathFor(loadedBefore.PrimaryCatalogPath));

        var loadedAfter = new CatalogLayerLoader(new SourceManager(paths), paths).Load();
        var checksum = loadedAfter.EffectiveChecksum(loadedAfter.Catalog.Assets.Single());
        Assert.NotNull(checksum);
        Assert.StartsWith("sha256:", checksum);
    }

    private static void WriteMinimalCatalog(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "catalog.yaml"), "schemaVersion: \"1\"\ncatalogVersion: 0.1.0\n");
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
        return new CatalogLayerLoader(new SourceManager(paths), paths).Load();
    }
}
