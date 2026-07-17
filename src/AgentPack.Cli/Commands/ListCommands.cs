using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class ListCommand : Command<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[kind]")]
        [Description("Filter by kind (skills, hooks, mcp, tools, instructions, rules, prompts, templates) or 'all'.")]
        public string? Kind { get; set; }

        [CommandOption("-g|--group <GROUP>")]
        [Description("Filter by group (repeatable or comma-separated).")]
        public string[] Groups { get; set; } = [];

        [CommandOption("-p|--provider <PROVIDER>")]
        [Description("Filter by provider.")]
        public string[] Providers { get; set; } = [];
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();

        var providerFilter = settings.Providers
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ProviderNames.Parse)
            .ToList();

        var targets = string.IsNullOrWhiteSpace(settings.Kind) ? Array.Empty<string>() : [settings.Kind];
        if (targets.Length > 0 && !settings.Kind!.Equals("all", StringComparison.OrdinalIgnoreCase) && !AssetKinds.TryParse(settings.Kind, out _))
        {
            throw new AgentPackException(
                $"Unknown kind '{settings.Kind}'.",
                $"Valid kinds: {string.Join(", ", AssetKinds.All.Select(AssetKinds.Display))} or 'all'.");
        }

        var assets = CommandHelpers.SelectAssets(loaded.Catalog, targets, settings.Groups, providerFilter.Count > 0 ? providerFilter : null);

        // The common case is all-recommended, all-local: those columns then say nothing — drop them.
        var showStatus = assets.Any(x => x.Status != AssetStatus.Recommended);
        var showSource = assets.Any(x => x.Source is AssetSource.External);
        var descriptionRoom = Math.Clamp(Spectre.Console.AnsiConsole.Profile.Width - 60, 24, 120);

        var headers = new List<string> { "ID", "Kind", "Version", "Groups", "Providers" };
        if (showStatus) headers.Add("Status");
        if (showSource) headers.Add("Source");
        headers.Add("Description");

        var filtered = targets.Length > 0 || settings.Groups.Length > 0 || providerFilter.Count > 0;
        Output.Table(
            headers.ToArray(),
            assets.Select(asset =>
            {
                var row = new List<string>
                {
                    asset.Id,
                    asset.Kind.Display(),
                    asset.Version.ToString(),
                    string.Join(",", asset.Groups),
                    asset.Providers.Count == ProviderNames.All.Count ? "all" : string.Join(",", asset.Providers.Select(ProviderNames.Display))
                };
                if (showStatus)
                {
                    row.Add(asset.Status switch
                    {
                        AssetStatus.Deprecated => "[yellow]deprecated[/]",
                        AssetStatus.Blocked => "[red]blocked[/]",
                        AssetStatus.Experimental => "[grey]experimental[/]",
                        _ => "recommended"
                    });
                }
                if (showSource) row.Add(asset.Source is AssetSource.External ? "external" : "local");
                row.Add(Output.Fit(asset.Description, descriptionRoom));
                return row.ToArray();
            }),
            emptyMessage: filtered
                ? "No assets match these filters. Run 'agentpack list' without filters to see everything."
                : "The catalog is empty. Scaffold an asset with 'agentpack new' or add a source with 'agentpack source add'.",
            markupColumns: showStatus ? [5] : null);

        if (assets.Count >= 10)
        {
            Output.Info($"{assets.Count} assets — narrow with 'agentpack list <kind>', --group <name>, or --provider <name>.");
        }

        return 0;
    }
}

public sealed class GroupsCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        Output.Table(
            ["ID", "Name", "Status", "ReplacedBy", "RemoveAfter"],
            loaded.Catalog.Groups.Select(x => new[]
            {
                x.Id, x.Name, x.Status.ToString().ToLowerInvariant(), x.ReplacedBy ?? "", x.RemoveAfter ?? ""
            }));
        return 0;
    }
}

public sealed class StatusCommand : Command<StatusCommand.Settings>
{
    public sealed class Settings : ScopeSettings;

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var scope = settings.ResolveScope(session.Paths);
        var lockFile = JsonStore.Load<AgentPackLock>(session.Paths.GetLockPath(scope));
        var assets = loaded.Catalog.Assets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var scopeName = scope == InstallScope.User ? "user" : "project";

        var updates = 0;
        var removed = 0;
        Output.Table(
            ["ID", "Kind", "Provider", "Installed", "Latest", "Pinned", "State"],
            lockFile.Entries.OrderBy(x => x.Id, StringComparer.Ordinal).Select(entry =>
            {
                assets.TryGetValue(entry.Id, out var asset);
                var latest = asset?.Version.ToString() ?? "(removed from catalog)";
                string state;
                if (asset is null)
                {
                    removed++;
                    state = "[red]removed from catalog[/]";
                }
                else if (asset.Version.IsNewerThan(entry.Version))
                {
                    updates++;
                    state = "[blue]update available[/]";
                }
                else
                {
                    state = "installed";
                }

                return new[]
                {
                    entry.Id, entry.Kind.Display(), entry.Provider.Display(),
                    entry.Version, latest, entry.Pinned ? "yes" : "no", state
                };
            }),
            emptyMessage: $"Nothing installed in {scopeName} scope yet. Run 'agentpack add' to pick assets.",
            markupColumns: [6]);

        if (lockFile.Entries.Count > 0)
        {
            var summary = $"{lockFile.Entries.Count} installed ({scopeName} scope)";
            if (updates > 0) summary += $" — {updates} update{(updates == 1 ? "" : "s")} available, run 'agentpack upgrade'";
            if (removed > 0) summary += $" — {removed} no longer in the catalog";
            Output.Info(summary + ".");
        }

        return 0;
    }
}

public sealed class DiffCommand : Command<DiffCommand.Settings>
{
    public sealed class Settings : ScopeSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Installed asset id to check for local modifications.")]
        public string Id { get; set; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var scope = settings.ResolveScope(session.Paths);
        var lockFile = JsonStore.Load<AgentPackLock>(session.Paths.GetLockPath(scope));
        var installer = new Installer(session.Paths);
        var root = installer.ScopeRoot(scope);

        var matches = lockFile.Entries.Where(x => x.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            Output.Info($"No install found for '{settings.Id}' in {(scope == InstallScope.User ? "user" : "project")} scope.");
            return 0;
        }

        Output.Table(
            ["ID", "Provider", "Target", "State"],
            matches.Select(entry =>
            {
                var installedPath = Installer.ResolveLockPath(entry.Path, root);
                var exists = File.Exists(installedPath) || Directory.Exists(installedPath);
                var state = !exists
                    ? "missing"
                    : Installer.InstalledFragmentState(entry, installedPath, scope) switch
                    {
                        FragmentState.Present => "clean",
                        FragmentState.Absent => "missing",
                        _ => "modified locally"
                    };
                return new[] { entry.Id, entry.Provider.Display(), entry.Path, state };
            }));
        return 0;
    }
}

public sealed class DoctorCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var detected = ProviderRegistry.Detect(session.Paths.WorkingDirectory);
        Output.Table(
            ["Check", "Value"],
            new[]
            {
                new[] { "AgentPack version", VersionInfo.Current },
                ["AgentPack home", session.Paths.Home],
                ["Working directory", session.Paths.WorkingDirectory],
                ["Git repository", CliSession.IsGitRepo(session.Paths.WorkingDirectory) ? "yes" : "no"],
                ["Detected providers", detected.Count > 0 ? string.Join(", ", detected.Select(ProviderNames.Display)) : "(none)"],
                ["Catalog sources", session.Sources.LoadConfig().Sources.Count.ToString()],
                ["Default scope", CliSession.IsGitRepo(session.Paths.WorkingDirectory) ? "project" : "user"]
            });
        return 0;
    }
}
