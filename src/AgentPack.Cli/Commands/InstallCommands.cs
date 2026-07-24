using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public class InstallCommand : Command<InstallCommand.Settings>
{
    public class Settings : ApplySettings
    {
        [CommandArgument(0, "[targets]")]
        [Description("Optional kind (skills, hooks, mcp, ...) followed by asset ids. Empty = interactive selection.")]
        public string[] Targets { get; set; } = [];

        [CommandOption("-g|--group <GROUP>")]
        [Description("Install everything in a group (repeatable or comma-separated).")]
        public string[] Groups { get; set; } = [];

        [CommandOption("--dry-run")]
        [Description("Show the install plan without changing provider files.")]
        public bool DryRun { get; set; }
    }

    protected virtual bool Apply => true;
    protected virtual string Title => "Install plan";

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var apply = Apply && !settings.DryRun;

        // Writing provider files off a stale catalog is worth a round trip; a dry run is
        // reporting, so it rides the normal cache like list/search/status do.
        var loaded = session.LoadCatalog(refreshRemoteNow: apply);
        var scope = settings.ResolveScope(session.Paths);
        var providers = settings.ResolveProviders(session.Paths, scope);

        Output.Info($"Catalog: {session.Sources.DescribeCatalog(loaded.PrimaryCatalogPath)}");

        var explicitIds = settings.Targets.Where(t => !AssetKinds.TryParse(t, out _) && t != "all").ToList();
        var filterGiven = settings.Targets.Length > 0 || settings.Groups.Length > 0;

        if (!filterGiven && settings.Yes)
        {
            throw new AgentPackException(
                "Refusing to install the entire catalog with --yes.",
                "Say what to install: ids, a kind ('agentpack install skills --yes'), or --group <name>.");
        }

        if (!filterGiven && !Output.CanPrompt)
        {
            throw new AgentPackException(
                "No assets specified and no interactive terminal to pick from.",
                "Pass asset ids ('agentpack install code-review'), a kind ('agentpack install skills'), or --group <name>.");
        }

        var assets = CommandHelpers.EnforceStatus(
            CommandHelpers.SelectAssets(loaded.Catalog, settings.Targets, settings.Groups, providers),
            explicitIds);
        if (assets.Count == 0)
        {
            throw new AgentPackException("No matching assets found.", "Run 'agentpack list' to see the catalog.");
        }

        // Named ids install directly. A kind or group (or nothing) opens the checklist,
        // filtered to that selection, so 'agentpack install skills --claude' lets you pick.
        if (apply && explicitIds.Count == 0 && assets.Count > 1 && Output.CanPrompt && !settings.Yes)
        {
            var pickerTitle = settings.Targets.Length > 0 || settings.Groups.Length > 0
                ? "Select assets to install (filtered)"
                : "Select assets to install";
            var installed = InstalledMarkers(JsonStore.Load<AgentPackLock>(session.Paths.GetLockPath(scope)), assets);
            assets = Prompts.SelectAssets(assets, pickerTitle, preselectAll: false, installed).ToList();
            if (assets.Count == 0)
            {
                Output.Info("Nothing selected.");
                return 0;
            }
        }

        var plan = new Installer(session.Paths).Plan(loaded, assets, providers, scope);
        var title = apply ? Title : "Install plan (dry run)";
        return CommandHelpers.RenderAndApply(session, loaded, plan, scope, settings, title, apply);
    }

    /// <summary>
    /// Marks which offered assets already exist in the scope's lockfile, and whether the
    /// catalog carries a newer version, so the picker can label rows install vs update.
    /// </summary>
    private static Dictionary<string, AssetInstallMarker> InstalledMarkers(AgentPackLock lockFile, IEnumerable<Asset> assets)
    {
        var entriesById = lockFile.Entries
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var markers = new Dictionary<string, AssetInstallMarker>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (!entriesById.TryGetValue(asset.Id, out var entries)) continue;
            markers[asset.Id] = entries.Any(entry => asset.Version.IsNewerThan(entry.Version))
                ? AssetInstallMarker.UpdateAvailable
                : AssetInstallMarker.Installed;
        }

        return markers;
    }
}

public sealed class RemoveCommand : Command<RemoveCommand.Settings>
{
    public sealed class Settings : ProviderScopeSettings
    {
        [CommandArgument(0, "<targets>")]
        [Description("Asset ids to remove, optionally preceded by a kind, or 'all'.")]
        public string[] Targets { get; set; } = [];
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var scope = settings.ResolveScope(session.Paths);
        var targets = settings.Targets.ToList();

        AssetKind? kind = null;
        if (targets.Count > 0 && AssetKinds.TryParse(targets[0], out var parsedKind))
        {
            kind = parsedKind;
            targets.RemoveAt(0);
        }
        else if (targets.Count > 0 && targets[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets.RemoveAt(0);
        }

        var removed = new Installer(session.Paths).Remove(kind, targets, settings.ExplicitProviders(), scope);
        if (removed.Count == 0)
        {
            Output.Info("Nothing to remove.");
            return targets.Count > 0 ? ExitCodes.UserError : ExitCodes.Ok;
        }

        var backups = scope == InstallScope.User ? "~/.agentpack/backups" : ".agentpack/backups";
        Output.Success($"Removed {removed.Count} install(s). Backups in {backups}.");
        Output.Table(
            ["ID", "Kind", "Provider", "Version", "Path"],
            removed.Select(x => new[] { x.Id, x.Kind.Display(), x.Provider.Display(), x.Version, x.Path }));
        Output.Info("Hook and MCP entries agentpack merged into shared provider configs were removed; the rest of those files was left untouched.");
        return 0;
    }
}

public class UpdateCommand : Command<UpdateCommand.Settings>
{
    public class Settings : ApplySettings
    {
        [CommandArgument(0, "[targets]")]
        [Description("Optional kind and/or asset ids to limit the update.")]
        public string[] Targets { get; set; } = [];
    }

    protected virtual bool Apply => true;

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();

        // 'update' applies, so it refreshes; 'outdated' only reports and uses the cache.
        var loaded = session.LoadCatalog(refreshRemoteNow: Apply);
        var scope = settings.ResolveScope(session.Paths);
        var explicitProviders = settings.ExplicitProviders();

        var installer = new Installer(session.Paths);
        var plan = installer.Outdated(loaded, scope, explicitProviders.Count > 0 ? explicitProviders : null);
        var items = plan.Items.ToList();

        var targets = settings.Targets.ToList();
        if (targets.Count > 0 && AssetKinds.TryParse(targets[0], out var kind))
        {
            items = items.Where(x => x.Asset.Kind == kind).ToList();
            targets.RemoveAt(0);
        }
        else if (targets.Count > 0 && targets[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets.RemoveAt(0);
        }

        if (targets.Count > 0)
        {
            items = items.Where(x => targets.Contains(x.Asset.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        // Interactive checklist when upgrading everything from a terminal.
        if (Apply && settings.Targets.Length == 0 && items.Count > 1 && Output.CanPrompt && !settings.Yes)
        {
            var chosen = Prompts.SelectAssets(items.Select(x => x.Asset).Distinct().ToList(), "Select assets to update", preselectAll: true);
            var chosenIds = chosen.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(x => chosenIds.Contains(x.Asset.Id)).ToList();
        }

        var filtered = new InstallPlan(items, plan.Skipped);
        return CommandHelpers.RenderAndApply(session, loaded, filtered, scope, settings, Apply ? "Update plan" : "Outdated", Apply);
    }
}

public sealed class OutdatedCommand : UpdateCommand
{
    protected override bool Apply => false;
}

public sealed class PinCommand : Command<PinCommand.Settings>
{
    public sealed class Settings : ScopeSettings
    {
        [CommandArgument(0, "<id>")]
        public string Id { get; set; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        new Installer(session.Paths).SetPinned(settings.Id, pinned: true, settings.ResolveScope(session.Paths));
        Output.Success($"Pinned '{settings.Id}'. Upgrades will skip it until you unpin.");
        return 0;
    }
}

public sealed class UnpinCommand : Command<PinCommand.Settings>
{
    public override int Execute(CommandContext context, PinCommand.Settings settings)
    {
        var session = new CliSession();
        new Installer(session.Paths).SetPinned(settings.Id, pinned: false, settings.ResolveScope(session.Paths));
        Output.Success($"Unpinned '{settings.Id}'.");
        return 0;
    }
}
