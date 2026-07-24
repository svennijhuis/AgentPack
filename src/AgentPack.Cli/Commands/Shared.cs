using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using AgentPack.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

/// <summary>Per-invocation environment: paths, sources, and the loaded catalog.</summary>
public sealed class CliSession
{
    public CliSession()
    {
        Paths = new AgentPackPaths();
        Sources = new SourceManager(Paths, AgentPackDefaults.OfficialCatalog());
    }

    public AgentPackPaths Paths { get; }
    public SourceManager Sources { get; }

    public LoadedCatalog LoadCatalog(bool refreshRemoteNow = false)
    {
        var loaded = new CatalogLayerLoader(Sources).Load(refreshRemoteNow: refreshRemoteNow);
        Output.Warnings(loaded.Warnings);
        if (loaded.Catalog.MinimumAgentPackVersion is { } minimum &&
            SemVersion.TryParse(VersionInfo.Current, out var current) && current < minimum)
        {
            throw new AgentPackException(
                $"This catalog requires AgentPack {minimum} or newer; you are running {current}.",
                "Update with 'dotnet tool update -g AgentPack' and retry.");
        }
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
        foreach (var value in CommandHelpers.SplitList(Provider))
        {
            providers.Add(ProviderNames.Parse(value));
        }

        return providers.Distinct().ToList();
    }

    /// <summary>
    /// Explicit flags win; otherwise providers are detected from the root the install
    /// actually writes to — the home directory for user scope, the repository for project
    /// scope. Detecting only in the working directory made 'install --user' fail in any
    /// folder that happened to have no provider config, which is the common global case.
    /// </summary>
    public IReadOnlyList<ProviderName> ResolveProviders(AgentPackPaths paths, InstallScope scope)
    {
        var explicitProviders = ExplicitProviders();
        if (explicitProviders.Count > 0) return explicitProviders;

        var root = scope == InstallScope.User ? paths.ProviderHome : paths.WorkingDirectory;
        var detected = ProviderRegistry.Detect(root);
        if (detected.Count > 0) return detected;

        // A project checkout with no provider files still installs for whatever the
        // developer already uses, so fall back to the other root before giving up.
        var fallbackRoot = scope == InstallScope.User ? paths.WorkingDirectory : paths.ProviderHome;
        detected = ProviderRegistry.Detect(fallbackRoot);
        if (detected.Count > 0) return detected;

        throw new AgentPackException(
            $"No provider configuration detected in {root}.",
            "Pass --claude, --codex, --copilot, --cursor, or -p <name>. 'agentpack doctor' shows what is detected.");
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
    /// Expands a repeatable option into its values: every command accepts both
    /// '-g a -g b' and '-g a,b', so the split lives in one place.
    /// </summary>
    public static IEnumerable<string> SplitList(IEnumerable<string> values) =>
        values.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Resolves list/install targets: [kind] [id...] where the kind is optional
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

        var groupList = SplitList(groups).ToList();
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
                var knownSet = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);

                // An id that exists in the catalog but is absent here was excluded by the
                // active kind/group/provider filter — it is not unknown, so it never gets a
                // "did you mean" hint pointing at itself.
                var filteredOut = missing.Where(knownSet.Contains).ToList();
                var unknown = missing.Where(id => !knownSet.Contains(id)).ToList();

                if (unknown.Count > 0)
                {
                    var suggestions = unknown
                        .Select(id => Suggestions.Nearest(id, known) is { } near ? $"'{id}' (did you mean '{near}'?)" : $"'{id}'");
                    throw new AgentPackException(
                        $"Unknown asset id(s): {string.Join(", ", suggestions)}.",
                        "Run 'agentpack list' to see the catalog.");
                }

                throw new AgentPackException(
                    $"Asset id(s) not available under the current filters: {string.Join(", ", filteredOut.Select(x => $"'{x}'"))}.",
                    "These exist in the catalog but don't match the requested kind, group, or provider. Run 'agentpack list' to see where they apply.");
            }

            return byId;
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

        if (!settings.Yes && Output.CanPrompt && !Prompts.ConfirmApply(actionable))
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

        var copilotRepoAgents = applied
            .Where(x => x.Provider == ProviderName.Copilot && x.Asset.Kind == AssetKind.Agents && scope == InstallScope.Project)
            .Select(x => x.Asset.Id)
            .ToList();
        if (copilotRepoAgents.Count > 0)
        {
            Output.Info($"Next step (copilot): commit .github/agents/ ({string.Join(", ", copilotRepoAgents)}). " +
                        "Copilot CLI picks it up immediately; the Copilot cloud coding agent reads custom agents from the default branch only.");
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
