using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public class AddCommand : Command<AddCommand.Settings>
{
    public class Settings : ApplySettings
    {
        [CommandArgument(0, "[targets]")]
        [Description("Optional kind (skills, hooks, mcp, ...) followed by asset ids. Empty = interactive selection.")]
        public string[] Targets { get; set; } = [];

        [CommandOption("-g|--group <GROUP>")]
        [Description("Install everything in a group (repeatable or comma-separated).")]
        public string[] Groups { get; set; } = [];
    }

    protected virtual bool Apply => true;
    protected virtual string Title => "Install plan";

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var scope = settings.ResolveScope(session.Paths);
        var providers = settings.ResolveProviders(session.Paths);

        var explicitIds = settings.Targets.Where(t => !AssetKinds.TryParse(t, out _) && t != "all").ToList();
        var filterGiven = settings.Targets.Length > 0 || settings.Groups.Length > 0;

        if (!filterGiven && settings.Yes)
        {
            throw new AgentPackException(
                "Refusing to install the entire catalog with --yes.",
                "Say what to install: ids, a kind ('agentpack add skills --yes'), or --group <name>.");
        }

        if (!filterGiven && !Output.CanPrompt)
        {
            throw new AgentPackException(
                "No assets specified and no interactive terminal to pick from.",
                "Pass asset ids ('agentpack add grill-me'), a kind ('agentpack add skills'), or --group <name>.");
        }

        var assets = CommandHelpers.EnforceStatus(
            CommandHelpers.SelectAssets(loaded.Catalog, settings.Targets, settings.Groups, providers),
            explicitIds);
        if (assets.Count == 0)
        {
            throw new AgentPackException("No matching assets found.", "Run 'agentpack list' to see the catalog.");
        }

        // Named ids install directly. A kind or group (or nothing) opens the checklist,
        // filtered to that selection, so 'agentpack add skills --claude' lets you pick.
        if (Apply && explicitIds.Count == 0 && assets.Count > 1 && Output.CanPrompt && !settings.Yes)
        {
            var title = settings.Targets.Length > 0 || settings.Groups.Length > 0
                ? "Select assets to install (filtered)"
                : "Select assets to install";
            assets = Prompts.SelectAssets(assets, title, preselectAll: false).ToList();
            if (assets.Count == 0)
            {
                Output.Info("Nothing selected.");
                return 0;
            }
        }

        var plan = new Installer(session.Paths).Plan(loaded, assets, providers, scope);
        return CommandHelpers.RenderAndApply(session, loaded, plan, scope, settings, Title, Apply);
    }
}

public sealed class PlanCommand : AddCommand
{
    protected override bool Apply => false;
    protected override string Title => "Plan (dry run)";
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

public class UpgradeCommand : Command<UpgradeCommand.Settings>
{
    public class Settings : ApplySettings
    {
        [CommandArgument(0, "[targets]")]
        [Description("Optional kind and/or asset ids to limit the upgrade.")]
        public string[] Targets { get; set; } = [];
    }

    protected virtual bool Apply => true;

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
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
            var chosen = Prompts.SelectAssets(items.Select(x => x.Asset).Distinct().ToList(), "Select assets to upgrade", preselectAll: true);
            var chosenIds = chosen.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items.Where(x => chosenIds.Contains(x.Asset.Id)).ToList();
        }

        var filtered = new InstallPlan(items, plan.Skipped);
        return CommandHelpers.RenderAndApply(session, loaded, filtered, scope, settings, Apply ? "Upgrade plan" : "Outdated", Apply);
    }
}

public sealed class OutdatedCommand : UpgradeCommand
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
