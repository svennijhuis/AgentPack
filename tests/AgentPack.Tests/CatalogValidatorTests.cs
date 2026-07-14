using AgentPack.Core;

namespace AgentPack.Tests;

public class CatalogValidatorTests
{
    [Fact]
    public void HookTriggerMustSupportEveryTargetProvider()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "notify",
            hook: new HookSpec { Trigger = HookTrigger.Notification, Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);

        var report = new CatalogValidator().Validate(loaded, verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "asset.hook.provider.incompatible");
    }

    [Fact]
    public void HookRequiresTypedMetadata()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard");

        var report = new CatalogValidator().Validate(TestData.Loaded(paths.WorkingDirectory, hook), verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "asset.hook.missing");
    }

    private const string Sha = "9d2f1ae187231d8199c64b5b762e1bdf2244733d";

    [Theory]
    [InlineData("main", false)]
    [InlineData("master", false)]
    [InlineData("refs/heads/feature", false)]
    [InlineData("latest", false)]
    [InlineData(Sha, true)]
    [InlineData("v1.2.0", true)]
    [InlineData("refs/tags/v1.2.0", true)]
    public void PinnedRefRules(string reference, bool pinned)
    {
        Assert.Equal(pinned, CatalogValidator.IsPinnedExternalRef(reference));
    }

    [Fact]
    public void MovingExternalRefIsAnError()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Skills, "floaty",
            source: new AssetSource.External("https://github.com/o/r.git", "main", "skills/x", null, null));
        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset), verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "asset.external.ref.moving" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void TagRefWarnsButPasses()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Skills, "tagged",
            source: new AssetSource.External("https://github.com/o/r.git", "v1.0.0", "skills/x", "sha256:" + new string('0', 64), "MIT"));
        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset), verifyChecksums: false);

        Assert.True(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "asset.external.ref.tag");
    }

    [Fact]
    public void MissingLicenseOnExternalWarns()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Skills, "unlicensed",
            source: new AssetSource.External("https://github.com/o/r.git", Sha, "skills/x", null, null));
        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset), verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "asset.external.license.missing" && x.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void LocalChecksumMismatchIsAnError()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Skills, "drifted");
        asset = asset with { Source = new AssetSource.Local(((AssetSource.Local)asset.Source).RelativePath, "sha256:" + new string('0', 64)) };

        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset));
        Assert.Contains(report.Issues, x => x.Code == "asset.checksum.mismatch" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void MissingChecksumWarnsToRunCatalogLock()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Skills, "unhashed");
        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset));

        Assert.True(report.IsValid);
        Assert.Contains(report.Issues, x => x.Code == "asset.checksum.missing");
    }

    [Fact]
    public void DeprecatedGroupRequiresReplacementAndRemovalDate()
    {
        using var temp = new TempDir();
        var catalog = new Catalog
        {
            Groups = [new GroupDefinition { Id = "api", Name = "API", Status = GroupStatus.Deprecated }]
        };
        var loaded = new LoadedCatalog(catalog, Path.Combine(temp.Path, "catalog.yaml"), [temp.Path], new CatalogLockFile(), []);
        var report = new CatalogValidator().Validate(loaded, verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "group.deprecated.incomplete" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void McpEnvVarsThatLookLikeValuesAreRejected()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Mcp, "leaky",
            mcp: new McpServer { Server = "leaky", Command = "run", EnvVars = ["TOKEN=abc123"] });
        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset));

        Assert.Contains(report.Issues, x => x.Code == "asset.mcp.envVar.invalid" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void AssetWithNoInstallableProviderIsAnError()
    {
        using var temp = new TempDir();
        // Rules limited to providers without rules files: nothing can install it.
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Rules, "nowhere",
            files: new Dictionary<string, string> { ["nowhere.mdc"] = "rule\n" });
        asset = asset with { Providers = [ProviderName.Codex, ProviderName.Copilot, ProviderName.Claude] };

        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, asset));
        Assert.Contains(report.Issues, x => x.Code == "asset.provider.none" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void ProfileReferencingUnknownAssetIsAnError()
    {
        using var temp = new TempDir();
        var catalog = new Catalog
        {
            Profiles = [new ProfileDefinition { Id = "backend", Name = "Backend", Assets = ["ghost"] }]
        };
        var loaded = new LoadedCatalog(catalog, Path.Combine(temp.Path, "catalog.yaml"), [temp.Path], new CatalogLockFile(), []);
        var report = new CatalogValidator().Validate(loaded, verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "profile.asset.unknown" && x.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void AgentRequiresDescription()
    {
        using var temp = new TempDir();
        var agent = TestData.WriteLocalAsset(temp.Path, AssetKind.Agents, "reviewer", agent: new AgentSpec()) with
        {
            Description = ""
        };

        var report = new CatalogValidator().Validate(TestData.Loaded(temp.Path, agent), verifyChecksums: false);

        Assert.Contains(report.Issues, x => x.Code == "agent.description.missing" && x.Severity == IssueSeverity.Error);
    }
}
