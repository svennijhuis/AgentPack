using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

/// <summary>Per-invocation environment: paths, sources, and the loaded catalog.</summary>
public sealed class CliSession
{
    public CliSession()
    {
        Paths = new AgentPackPaths();
        Sources = new SourceManager(Paths);
    }

    public AgentPackPaths Paths { get; }
    public SourceManager Sources { get; }

    public LoadedCatalog LoadCatalog()
    {
        var loaded = new CatalogLayerLoader(Sources, Paths).Load();
        Output.Warnings(loaded.Warnings);
        return loaded;
    }

    public static bool IsGitRepo(string root) =>
        Directory.Exists(Path.Combine(root, ".git")) ||
        ProcessRunner.Run("git", ["rev-parse", "--is-inside-work-tree"], root).ExitCode == 0;
}

/// <summary>Options shared by every command that installs, removes, or inspects installs.</summary>
public class ScopeSettings : CommandSettings
{
    [CommandOption("--user")]
    [Description("Operate on user scope (your home directory) instead of the project.")]
    public bool User { get; set; }

    [CommandOption("--project")]
    [Description("Operate on project scope (the current repository).")]
    public bool Project { get; set; }

    public InstallScope ResolveScope(AgentPackPaths paths)
    {
        if (User && Project)
        {
            throw new AgentPackException("--user and --project cannot be combined.");
        }

        if (User) return InstallScope.User;
        if (Project) return InstallScope.Project;
        return CliSession.IsGitRepo(paths.WorkingDirectory) ? InstallScope.Project : InstallScope.User;
    }
}

/// <summary>Scope options plus provider selection flags.</summary>
public class ProviderScopeSettings : ScopeSettings
{
    [CommandOption("--claude")]
    [Description("Target Claude Code.")]
    public bool Claude { get; set; }

    [CommandOption("--codex")]
    [Description("Target Codex.")]
    public bool Codex { get; set; }

    [CommandOption("--copilot")]
    [Description("Target GitHub Copilot.")]
    public bool Copilot { get; set; }

    [CommandOption("--cursor")]
    [Description("Target Cursor.")]
    public bool Cursor { get; set; }

    [CommandOption("-p|--provider <PROVIDER>")]
    [Description("Target providers by name (repeatable or comma-separated).")]
    public string[] Provider { get; set; } = [];

    public IReadOnlyList<ProviderName> ExplicitProviders()
    {
        var providers = new List<ProviderName>();
        if (Claude) providers.Add(ProviderName.Claude);
        if (Codex) providers.Add(ProviderName.Codex);
        if (Copilot) providers.Add(ProviderName.Copilot);
        if (Cursor) providers.Add(ProviderName.Cursor);
        foreach (var value in Provider.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            providers.Add(ProviderNames.Parse(value));
        }

        return providers.Distinct().ToList();
    }

    /// <summary>Explicit flags win; otherwise providers detected from the working directory.</summary>
    public IReadOnlyList<ProviderName> ResolveProviders(AgentPackPaths paths)
    {
        var explicitProviders = ExplicitProviders();
        if (explicitProviders.Count > 0) return explicitProviders;

        var detected = ProviderRegistry.Detect(paths.WorkingDirectory);
        if (detected.Count == 0)
        {
            throw new AgentPackException(
                "No provider configuration detected in this directory.",
                "Pass --claude, --codex, --copilot, --cursor, or -p <name>. 'agentpack doctor' shows what is detected.");
        }

        return detected;
    }
}

/// <summary>Provider + scope options plus apply-behavior flags.</summary>
public class ApplySettings : ProviderScopeSettings
{
    [CommandOption("-y|--yes")]
    [Description("Skip confirmation prompts (for scripts and CI).")]
    public bool Yes { get; set; }

    [CommandOption("--force")]
    [Description("Overwrite locally modified installs without asking.")]
    public bool Force { get; set; }

    [CommandOption("--keep-local")]
    [Description("Keep locally modified installs without asking.")]
    public bool KeepLocal { get; set; }

    public override ValidationResult Validate()
    {
        return Force && KeepLocal
            ? ValidationResult.Error("--force and --keep-local cannot be combined.")
            : ValidationResult.Success();
    }
}

public static class CommandHelpers
{
    /// <summary>
    /// Resolves list/add style targets: [kind] [id...] where the kind is optional
    /// and unknown ids get a "did you mean" suggestion.
    /// </summary>
    public static List<Asset> SelectAssets(Catalog catalog, string[] targets, string[] groups, IReadOnlyList<ProviderName>? providerFilter)
    {
        var assets = catalog.Assets.AsEnumerable();
        var ids = targets.ToList();

        AssetKind? kind = null;
        if (ids.Count > 0 && !ids[0].Equals("all", StringComparison.OrdinalIgnoreCase) && AssetKinds.TryParse(ids[0], out var parsedKind))
        {
            kind = parsedKind;
            ids.RemoveAt(0);
        }
        else if (ids.Count > 0 && ids[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            ids.RemoveAt(0);
        }

        if (kind is not null) assets = assets.Where(x => x.Kind == kind);

        var groupList = groups.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (groupList.Count > 0)
        {
            assets = assets.Where(x => x.Groups.Any(g => groupList.Contains(g, StringComparer.OrdinalIgnoreCase)));
        }

        if (providerFilter is { Count: > 0 })
        {
            assets = assets.Where(x => providerFilter.Any(p => x.Providers.Contains(p)));
        }

        var result = assets.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        if (ids.Count > 0)
        {
            var byId = result.Where(x => ids.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).ToList();
            var missing = ids.Where(id => !byId.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
            {
                var known = catalog.Assets.Select(x => x.Id).ToList();
                var suggestions = missing
                    .Select(id => Suggestions.Nearest(id, known) is { } near ? $"'{id}' (did you mean '{near}'?)" : $"'{id}'");
                throw new AgentPackException(
                    $"Unknown asset id(s): {string.Join(", ", suggestions)}.",
                    "Run 'agentpack list' to see the catalog.");
            }

            return byId;
        }

        return result;
    }

    /// <summary>Blocked assets never install; deprecated ones warn. Explicitly requested blocked assets are an error.</summary>
    public static List<Asset> EnforceStatus(List<Asset> assets, IReadOnlyList<string> explicitIds)
    {
        var result = new List<Asset>();
        foreach (var asset in assets)
        {
            switch (asset.Status)
            {
                case AssetStatus.Blocked when explicitIds.Contains(asset.Id, StringComparer.OrdinalIgnoreCase):
                    throw new AgentPackException(
                        $"Asset '{asset.Id}' is blocked by the catalog and cannot be installed.",
                        "Ask the asset owner or your platform team why it is blocked.");
                case AssetStatus.Blocked:
                    Output.Info($"Skipping '{asset.Id}': status is blocked.");
                    continue;
                case AssetStatus.Deprecated:
                    Output.Warning($"'{asset.Id}' is deprecated.");
                    break;
            }

            result.Add(asset);
        }

        return result;
    }

    /// <summary>Shared apply pipeline: render plan, confirm, resolve drift, apply, report.</summary>
    public static int RenderAndApply(
        CliSession session,
        LoadedCatalog loaded,
        InstallPlan plan,
        InstallScope scope,
        ApplySettings settings,
        string title,
        bool apply)
    {
        Output.Plan(title, plan, scope, session.Paths.WorkingDirectory, session.Paths.ProviderHome);
        if (!apply || plan.Items.Count == 0) return 0;

        // Output.Plan already reported "Everything is already up to date." for this case.
        var actionable = plan.Items.Where(x => x.State != InstallState.Installed && x.State != InstallState.Pinned).ToList();
        if (actionable.Count == 0)
        {
            return 0;
        }

        if (!settings.Yes && Output.CanPrompt && !Prompts.Confirm("Apply these changes?"))
        {
            Output.Info("Nothing applied.");
            return 0;
        }

        var installer = new Installer(session.Paths);
        var scopeRoot = installer.ScopeRoot(scope);
        var results = installer.Apply(plan.Items, loaded, scope, item => ResolveDrift(item, settings, scope, scopeRoot));
        Output.ApplyResults(results);
        PrintFollowUps(results, scope);
        return 0;
    }

    /// <summary>Provider-specific steps the user still has to do after a successful apply.</summary>
    private static void PrintFollowUps(IReadOnlyList<ApplyResult> results, InstallScope scope)
    {
        var applied = results
            .Where(x => x.Outcome is ApplyOutcome.Installed or ApplyOutcome.Updated)
            .Select(x => x.Item)
            .ToList();

        var copilotRepoHooks = applied
            .Where(x => x.Provider == ProviderName.Copilot && x.Asset.Kind == AssetKind.Hooks && scope == InstallScope.Project)
            .Select(x => x.Asset.Id)
            .ToList();
        if (copilotRepoHooks.Count > 0)
        {
            Output.Info($"Next step (copilot): commit .github/hooks/ ({string.Join(", ", copilotRepoHooks)}). " +
                        "Copilot CLI picks it up immediately; the Copilot cloud coding agent reads hooks from the default branch only.");
        }

        var envVars = applied
            .Where(x => x.Asset.Mcp is not null)
            .SelectMany(x => x.Asset.Mcp!.EnvVars.Concat(x.Asset.Mcp!.HeaderEnvVars.Values))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (envVars.Count > 0)
        {
            Output.Info($"Next step (mcp): set {string.Join(", ", envVars)} in your environment — values are never stored in provider configs.");
        }

        if (scope == InstallScope.Project && applied.Any(x => x.Provider == ProviderName.Claude && x.Asset.Kind == AssetKind.Mcp))
        {
            Output.Info("Next step (claude): Claude Code asks you to approve project .mcp.json servers on its next start — accept the prompt to activate them.");
        }
    }

    private static DriftAction ResolveDrift(InstallPlanItem item, ApplySettings settings, InstallScope scope, string scopeRoot)
    {
        if (settings.Force) return DriftAction.Overwrite;
        if (settings.KeepLocal) return DriftAction.Keep;
        if (!Output.CanPrompt || settings.Yes)
        {
            var reason = item.State == InstallState.UnmanagedPresent
                ? "the target already exists and was not installed by agentpack — keeping it"
                : "has local changes — keeping them";
            Output.Warning(
                $"{item.Asset.Id} ({item.Provider.Display()}): {reason}. " +
                "Use --force to overwrite or --keep-local to silence this warning.");
            return DriftAction.Keep;
        }

        return Prompts.ResolveDrift(item, scope, scopeRoot);
    }
}
