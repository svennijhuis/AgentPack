using System.Text.RegularExpressions;

namespace AgentPack.Core;

public sealed class ValidationReport
{
    public List<CatalogIssue> Issues { get; } = [];
    public bool IsValid => Issues.All(x => x.Severity != IssueSeverity.Error);
    public void Error(string code, string message) => Issues.Add(new CatalogIssue(IssueSeverity.Error, code, message));
    public void Warning(string code, string message) => Issues.Add(new CatalogIssue(IssueSeverity.Warning, code, message));
}

/// <summary>
/// Cross-entity validation on the typed catalog. Field-level parsing errors are
/// caught earlier by CatalogMapper; this checks relationships, content, and policy.
/// </summary>
public sealed class CatalogValidator
{
    private static readonly HashSet<string> MovingRefs = new(StringComparer.OrdinalIgnoreCase)
    {
        "main", "master", "develop", "development", "trunk", "head", "latest"
    };

    private static readonly Regex FullSha = new("^[0-9a-f]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public ValidationReport Validate(LoadedCatalog loaded, bool verifyChecksums = true)
    {
        var report = new ValidationReport();
        foreach (var warning in loaded.Warnings) report.Issues.Add(warning);

        ValidateGroups(loaded.Catalog, report);
        ValidateAssets(loaded, verifyChecksums, report);
        ValidateProfiles(loaded.Catalog, report);
        return report;
    }

    public static bool IsPinnedExternalRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (MovingRefs.Contains(reference)) return false;
        if (reference.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)) return false;
        if (FullSha.IsMatch(reference)) return true;
        return reference.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase) ||
               reference.StartsWith("v", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFullCommitSha(string? reference) => reference is not null && FullSha.IsMatch(reference);

    private static void ValidateGroups(Catalog catalog, ValidationReport report)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalog.Groups)
        {
            if (!seen.Add(group.Id)) report.Error("group.id.duplicate", $"Duplicate group id '{group.Id}'.");
            if (group.Status == GroupStatus.Deprecated &&
                (string.IsNullOrWhiteSpace(group.ReplacedBy) || string.IsNullOrWhiteSpace(group.RemoveAfter)))
            {
                report.Error("group.deprecated.incomplete", $"Deprecated group '{group.Id}' must define replacedBy and removeAfter.");
            }
        }
    }

    private static void ValidateAssets(LoadedCatalog loaded, bool verifyChecksums, ValidationReport report)
    {
        var catalog = loaded.Catalog;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupIds = catalog.Groups.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in catalog.Assets)
        {
            if (!seen.Add(asset.Id)) report.Error("asset.id.duplicate", $"Duplicate asset id '{asset.Id}'.");
            if (asset.Groups.Count == 0) report.Warning("asset.groups.empty", $"Asset '{asset.Id}' has no groups.");
            foreach (var group in asset.Groups.Where(g => groupIds.Count > 0 && !groupIds.Contains(g)))
            {
                report.Warning("asset.group.unknown", $"Asset '{asset.Id}' references unknown group '{group}'.");
            }

            var supported = asset.Providers.Where(p => ProviderRegistry.Get(p).Plan(asset, userScope: false) is ProviderPlan.Supported).ToList();
            if (supported.Count == 0)
            {
                report.Error("asset.provider.none",
                    $"No provider can install '{asset.Id}' ({asset.Kind.Display()}). " +
                    $"Unsupported everywhere it is listed: {string.Join(", ", asset.Providers.Select(ProviderNames.Display))}.");
            }

            ValidateMcp(asset, report);
            ValidateSource(loaded, asset, verifyChecksums, report);
        }
    }

    private static void ValidateMcp(Asset asset, ValidationReport report)
    {
        if (asset.Kind != AssetKind.Mcp || asset.Mcp is null) return;

        foreach (var envVar in asset.Mcp.EnvVars.Where(x => !EnvName.IsMatch(x)))
        {
            report.Error("asset.mcp.envVar.invalid", $"MCP asset '{asset.Id}' env var '{envVar}' must be a variable name, not a value.");
        }

        foreach (var (header, envVar) in asset.Mcp.HeaderEnvVars)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                report.Error("asset.mcp.header.required", $"MCP asset '{asset.Id}' has an empty header name.");
            }

            if (!EnvName.IsMatch(envVar))
            {
                report.Error("asset.mcp.headerEnvVar.invalid", $"MCP asset '{asset.Id}' header '{header}' must reference an env var name.");
            }
        }
    }

    private static void ValidateSource(LoadedCatalog loaded, Asset asset, bool verifyChecksums, ValidationReport report)
    {
        switch (asset.Source)
        {
            case AssetSource.Local local:
            {
                var fullPath = Path.GetFullPath(Path.Combine(loaded.RootFor(asset), local.RelativePath));
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    report.Error("asset.local.missing", $"Asset '{asset.Id}' local content does not exist: {local.RelativePath}.");
                    return;
                }

                var expected = loaded.EffectiveChecksum(asset);
                if (expected is null)
                {
                    report.Warning("asset.checksum.missing",
                        $"Asset '{asset.Id}' has no checksum in the manifest or catalog.lock.yaml. Run 'agentpack catalog lock'.");
                }
                else if (!expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error("asset.checksum.invalid", $"Asset '{asset.Id}' checksum must start with sha256:.");
                }
                else if (verifyChecksums)
                {
                    var actual = ContentHash.Compute(fullPath);
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Error("asset.checksum.mismatch",
                            $"Asset '{asset.Id}' checksum mismatch: expected {expected}, got {actual}. " +
                            "Rerun 'agentpack catalog lock' after content changes.");
                    }
                }

                break;
            }

            case AssetSource.External external:
            {
                ResolvedExternalSource resolved;
                try
                {
                    resolved = ExternalSourceParser.Resolve(external);
                }
                catch (AgentPackException ex)
                {
                    report.Error("asset.external.source.invalid", $"External asset '{asset.Id}' has an invalid source: {ex.Message}");
                    return;
                }

                if (!IsPinnedExternalRef(resolved.Ref))
                {
                    report.Error("asset.external.ref.moving",
                        $"External asset '{asset.Id}' must pin ref to a commit SHA or immutable tag, not '{resolved.Ref}'.");
                }
                else if (!IsFullCommitSha(resolved.Ref))
                {
                    report.Warning("asset.external.ref.tag", $"External asset '{asset.Id}' uses a tag. CI must verify the tag has not moved.");
                }

                if (external.License is null)
                {
                    report.Warning("asset.external.license.missing", $"External asset '{asset.Id}' does not record its upstream license.");
                }

                var expected = loaded.EffectiveChecksum(asset);
                if (expected is not null && !expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    report.Error("asset.checksum.invalid", $"Asset '{asset.Id}' checksum must start with sha256:.");
                }

                break;
            }
        }
    }

    private static void ValidateProfiles(Catalog catalog, ValidationReport report)
    {
        var assetIds = catalog.Assets.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groupIds = catalog.Groups.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in catalog.Profiles)
        {
            if (!seen.Add(profile.Id)) report.Error("profile.id.duplicate", $"Duplicate profile id '{profile.Id}'.");
            foreach (var asset in profile.Assets.Where(a => !assetIds.Contains(a)))
            {
                report.Error("profile.asset.unknown", $"Profile '{profile.Id}' references unknown asset '{asset}'.");
            }

            foreach (var group in profile.Groups.Where(g => groupIds.Count > 0 && !groupIds.Contains(g)))
            {
                report.Warning("profile.group.unknown", $"Profile '{profile.Id}' references unknown group '{group}'.");
            }
        }
    }
}
