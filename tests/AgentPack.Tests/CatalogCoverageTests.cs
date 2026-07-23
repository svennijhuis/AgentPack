using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// Guards the shipped official catalog (this repo's root catalog.yaml + assets/).
/// It must keep covering every installable kind with really-installable assets, so
/// it never silently regresses to a handful of examples.
/// </summary>
public class CatalogCoverageTests
{
    // Tools and templates are intentionally empty: no provider has a native
    // destination for them, and 'agentpack submit' rejects both.
    private static readonly AssetKind[] InstallableKinds =
        AssetKinds.All.Where(k => k is not (AssetKind.Tools or AssetKind.Templates)).ToArray();

    [Fact]
    public void EveryInstallableKindHasAtLeastOneAsset()
    {
        var catalog = LoadRootCatalog();
        var kindsPresent = catalog.Assets.Select(x => x.Kind).ToHashSet();

        var missing = InstallableKinds.Where(k => !kindsPresent.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "Official catalog is missing assets for: " + string.Join(", ", missing.Select(AssetKinds.Display)));
    }

    [Fact]
    public void EveryAssetInstallsOnAtLeastOneProvider()
    {
        var catalog = LoadRootCatalog();

        var orphans = catalog.Assets
            .Where(a => !a.Providers.Any(p => ProviderRegistry.Get(p).Plan(a, userScope: false) is ProviderPlan.Supported))
            .Select(a => a.Id)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These catalog assets install on no provider: " + string.Join(", ", orphans));
    }

    [Fact]
    public void RootCatalogHasNoValidationErrors()
    {
        var loaded = LoadRoot();
        // Structure/policy only; checksum hashing is covered by CI's 'catalog lock --check'.
        var report = new CatalogValidator().Validate(loaded, verifyChecksums: false);
        var errors = report.Issues.Where(x => x.Severity == IssueSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Root catalog has validation errors: " + string.Join("; ", errors.Select(x => $"[{x.Code}] {x.Message}")));
    }

    private static Catalog LoadRootCatalog() => LoadRoot().Catalog;

    private static LoadedCatalog LoadRoot()
    {
        var paths = new AgentPackPaths(workingDirectory: FindRepoRoot());
        return new CatalogLayerLoader(new SourceManager(paths)).Load();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AgentPack.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not find the AgentPack repository root.");
    }
}
