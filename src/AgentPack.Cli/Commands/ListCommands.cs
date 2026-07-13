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
        Output.Table(
            ["ID", "Kind", "Version", "Groups", "Providers", "Status", "Source", "Description"],
            assets.Select(asset => new[]
            {
                asset.Id,
                asset.Kind.Display(),
                asset.Version.ToString(),
                string.Join(",", asset.Groups),
                asset.Providers.Count == ProviderNames.All.Count ? "all" : string.Join(",", asset.Providers.Select(ProviderNames.Display)),
                asset.Status.ToString().ToLowerInvariant(),
                asset.Source is AssetSource.External ? "external" : "local",
                asset.Description
            }));
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

        Output.Table(
            ["ID", "Kind", "Provider", "Installed", "Latest", "Pinned", "State"],
            lockFile.Entries.OrderBy(x => x.Id, StringComparer.Ordinal).Select(entry =>
            {
                assets.TryGetValue(entry.Id, out var asset);
                var latest = asset?.Version.ToString() ?? "(removed from catalog)";
                var state = asset is null
                    ? "removed from catalog"
                    : asset.Version.IsNewerThan(entry.Version) ? "update available" : "installed";
                return new[]
                {
                    entry.Id, entry.Kind.Display(), entry.Provider.Display(),
                    entry.Version, latest, entry.Pinned ? "yes" : "no", state
                };
            }));
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
