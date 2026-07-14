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
            if (asset.Kind == AssetKind.Tools)
            {
                report.Error("asset.tools.unsupported",
                    $"Generic tools asset '{asset.Id}' is not supported. Package custom tools as an MCP asset with an explicit mcp.tools inventory.");
            }
            if (!seen.Add(asset.Id)) report.Error("asset.id.duplicate", $"Duplicate asset id '{asset.Id}'.");
            if (asset.Groups.Count == 0) report.Warning("asset.groups.empty", $"Asset '{asset.Id}' has no groups.");
            foreach (var group in asset.Groups.Where(g => groupIds.Count > 0 && !groupIds.Contains(g) && !groupIds.Contains(GroupMatch.TopLevel(g))))
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
            ValidateHook(asset, report);
            ValidateAgent(catalog, asset, report);
            ValidateSource(loaded, asset, verifyChecksums, report);
        }
    }

    private static void ValidateAgent(Catalog catalog, Asset asset, ValidationReport report)
    {
        if (asset.Kind != AssetKind.Agents)
        {
            if (asset.Agent is not null)
            {
                report.Error("agent.spec.kind", $"Asset '{asset.Id}' declares agent: but its kind is '{asset.Kind.Display()}'.");
            }
            return;
        }

        if (asset.Agent is null)
        {
            report.Error("agent.spec.missing", $"Agent '{asset.Id}' requires an agent: section.");
            return;
        }

        if (string.IsNullOrWhiteSpace(asset.Description))
        {
            report.Error("agent.description.missing",
                $"Agent '{asset.Id}' requires a non-empty top-level description so providers can decide when to delegate to it.");
        }

        var byId = catalog.Assets.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, expectedKind) in asset.Agent.Imports.All())
        {
            if (!byId.TryGetValue(reference.Id, out var matches))
            {
                report.Error("agent.import.missing",
                    $"Agent '{asset.Id}' imports {expectedKind.Display()} '{reference.Id}', but no catalog asset with that id exists.");
                continue;
            }

            if (matches.Count != 1)
            {
                report.Error("agent.import.ambiguous", $"Agent '{asset.Id}' imports ambiguous asset id '{reference.Id}'.");
                continue;
            }

            var dependency = matches[0];
            if (dependency.Kind != expectedKind)
            {
                report.Error("agent.import.kind",
                    $"Agent '{asset.Id}' imports '{reference.Id}' as {expectedKind.Display()}, but it is kind '{dependency.Kind.Display()}'.");
                continue;
            }

            if (dependency.Status == AssetStatus.Blocked)
            {
                report.Error("agent.import.blocked", $"Agent '{asset.Id}' imports blocked asset '{reference.Id}'.");
            }
            else if (dependency.Status == AssetStatus.Deprecated)
            {
                report.Error("agent.import.deprecated",
                    $"Agent '{asset.Id}' imports deprecated asset '{reference.Id}'; migrate the dependency before publishing the agent.");
            }

            if (reference.VersionRange is not null && !reference.VersionRange.Contains(dependency.Version))
            {
                report.Error("agent.dependency.version",
                    $"Agent '{asset.Id}' requires {expectedKind.Display()} '{reference.Id}' at {reference.VersionRange}, " +
                    $"but the effective catalog contains {dependency.Version}.");
            }

            var missingProviders = asset.Providers.Where(x => !dependency.Providers.Contains(x)).ToList();
            if (missingProviders.Count > 0)
            {
                report.Error("agent.provider.incompatible",
                    $"Agent '{asset.Id}' targets {string.Join(", ", missingProviders.Select(ProviderNames.Display))}, " +
                    $"but imported {expectedKind.Display()} '{reference.Id}' does not.");
            }

            if (expectedKind == AssetKind.Mcp && (dependency.Mcp is null || dependency.Mcp.Tools.Count == 0))
            {
                report.Error("agent.mcp.tools.missing",
                    $"MCP asset '{reference.Id}' is imported by agent '{asset.Id}' but does not declare mcp.tools.");
            }
        }
    }

    private static void ValidateMcp(Asset asset, ValidationReport report)
    {
        if (asset.Kind != AssetKind.Mcp)
        {
            if (asset.Mcp is not null)
                report.Error("asset.mcp.kind", $"Asset '{asset.Id}' declares mcp: but its kind is '{asset.Kind.Display()}'.");
            return;
        }
        if (asset.Mcp is null) return; // External MCP assets may supply a reviewed raw mcp.json.

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

    private static void ValidateHook(Asset asset, ValidationReport report)
    {
        if (asset.Kind != AssetKind.Hooks)
        {
            if (asset.Hook is not null)
                report.Error("asset.hook.kind", $"Asset '{asset.Id}' declares hook: but its kind is '{asset.Kind.Display()}'.");
            return;
        }

        if (asset.Hook is null)
        {
            report.Error("asset.hook.missing",
                $"Hook asset '{asset.Id}' requires typed hook metadata; upstream hook configuration is never trusted implicitly.");
            return;
        }

        if (string.IsNullOrWhiteSpace(asset.Hook.Command))
            report.Error("asset.hook.command.missing", $"Hook asset '{asset.Id}' requires hook.command.");

        if (asset.Hook.Trigger == HookTrigger.Notification)
        {
            var incompatible = asset.Providers
                .Where(x => x is ProviderName.Codex or ProviderName.Cursor)
                .ToList();
            if (incompatible.Count > 0)
            {
                report.Error("asset.hook.provider.incompatible",
                    $"Hook '{asset.Id}' uses notification, which is unsupported by " +
                    $"{string.Join(", ", incompatible.Select(ProviderNames.Display))}. " +
                    "Restrict providers or choose another trigger.");
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
                    if (expected is null)
                    {
                        report.Error("asset.external.checksum.missing",
                            $"External asset '{asset.Id}' is pinned but not checksummed. Run 'agentpack catalog lock' and commit catalog.lock.yaml.");
                    }
                    else if (!expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
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

            foreach (var group in profile.Groups.Where(g => groupIds.Count > 0 && !groupIds.Contains(g) && !groupIds.Contains(GroupMatch.TopLevel(g))))
            {
                report.Warning("profile.group.unknown", $"Profile '{profile.Id}' references unknown group '{group}'.");
            }
        }
    }
}
