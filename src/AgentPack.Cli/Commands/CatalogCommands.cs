using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class CatalogValidateCommand : Command<CatalogValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-checksums")]
        [Description("Skip content checksum verification (faster).")]
        public bool NoChecksums { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var report = new CatalogValidator().Validate(loaded, verifyChecksums: !settings.NoChecksums);
        Output.Report(report);
        return report.IsValid ? ExitCodes.Ok : ExitCodes.ValidationFailed;
    }
}

public sealed class CatalogLockCommand : Command<CatalogLockCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--no-fetch")]
        [Description("Skip fetching external assets (their checksums stay uncomputed).")]
        public bool NoFetch { get; set; }

        [CommandOption("--check")]
        [Description("Verify the existing catalog.lock.yaml is up to date; fail if not (for CI).")]
        public bool Check { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var writer = new CatalogLockWriter(session.Paths);
        var result = writer.Generate(loaded, fetchExternal: !settings.NoFetch);
        foreach (var message in result.Messages) Output.Warning(message);

        var lockPath = CatalogLockFile.PathFor(loaded.PrimaryCatalogPath);
        if (settings.Check)
        {
            var existing = CatalogLockFile.Load(lockPath);
            var upToDate = SameEntries(existing, result.Lock);
            if (!upToDate)
            {
                Output.Error("catalog.lock.yaml is out of date.", "Run 'agentpack catalog lock' and commit the result.");
                return ExitCodes.ValidationFailed;
            }

            Output.Success("catalog.lock.yaml is up to date.");
            return ExitCodes.Ok;
        }

        result.Lock.Save(lockPath);
        Output.Success($"Wrote {lockPath} ({result.Lock.Entries.Count} entries). Commit it with your changes.");
        return 0;
    }

    private static bool SameEntries(CatalogLockFile left, CatalogLockFile right)
    {
        if (left.Entries.Count != right.Entries.Count) return false;
        var byId = left.Entries.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in right.Entries)
        {
            if (!byId.TryGetValue(entry.Id, out var other)) return false;
            if (other.Checksum != entry.Checksum || other.Ref != entry.Ref || other.Url != entry.Url || other.SourceType != entry.SourceType)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class CatalogVerifyExternalCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var resolver = new ExternalResolver(session.Paths);
        var report = Output.CanPrompt
            ? AnsiConsole.Status().Start("Fetching external assets...", _ => resolver.VerifyExternal(loaded))
            : resolver.VerifyExternal(loaded);
        Output.Report(report);
        return report.IsValid ? ExitCodes.Ok : ExitCodes.ValidationFailed;
    }
}

public sealed class CatalogCompileCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var report = new CatalogValidator().Validate(loaded, verifyChecksums: true);
        if (!report.IsValid)
        {
            Output.Report(report);
            return ExitCodes.ValidationFailed;
        }

        var result = new CatalogCompiler(session.Paths).Compile(loaded);
        foreach (var warning in result.Warnings)
            Output.Warning($"[{warning.Code}] {warning.Message}");
        Output.Success($"Compiled and syntax-checked {result.Outputs.Count} native agent output(s).");
        Output.Table(["Agent", "Provider", "Scope", "Checksum"], result.Outputs.Select(x => new[]
        {
            x.Id, x.Provider.Display(), x.Scope.ToString().ToLowerInvariant(), x.Checksum
        }));
        return 0;
    }
}

public sealed class ProfileListCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        Output.Table(
            ["ID", "Name", "Providers", "Groups", "Assets"],
            loaded.Catalog.Profiles.Select(x => new[]
            {
                x.Id, x.Name,
                x.Providers.Count > 0 ? string.Join(",", x.Providers.Select(ProviderNames.Display)) : "(detected)",
                string.Join(",", x.Groups),
                string.Join(",", x.Assets)
            }));
        return 0;
    }
}

public class ProfileApplyCommand : Command<ProfileApplyCommand.Settings>
{
    public class Settings : ApplySettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Profile id from the catalog.")]
        public string Id { get; set; } = "";
    }

    protected virtual bool Apply => true;

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var loaded = session.LoadCatalog();
        var scope = settings.ResolveScope(session.Paths);

        var profile = loaded.Catalog.Profiles.FirstOrDefault(x => x.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentPackException(
                $"Unknown profile '{settings.Id}'." +
                (Suggestions.Nearest(settings.Id, loaded.Catalog.Profiles.Select(x => x.Id)) is { } near ? $" Did you mean '{near}'?" : ""),
                "Run 'agentpack profile list'.");

        var assets = Expand(loaded.Catalog, profile);
        assets = CommandHelpers.EnforceStatus(assets, explicitIds: []);
        if (assets.Count == 0)
        {
            throw new AgentPackException($"Profile '{profile.Id}' selects no installable assets.");
        }

        var providers = profile.Providers.Count > 0 ? profile.Providers : settings.ResolveProviders(session.Paths);
        var plan = new Installer(session.Paths).Plan(loaded, assets, providers, scope, $"profile:{profile.Id}");
        return CommandHelpers.RenderAndApply(session, loaded, plan, scope, settings, $"Profile '{profile.Id}'", Apply);
    }

    private static List<Asset> Expand(Catalog catalog, ProfileDefinition profile)
    {
        var byId = catalog.Assets.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var selected = new List<Asset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assetId in profile.Assets)
        {
            if (!byId.TryGetValue(assetId, out var asset))
            {
                throw new AgentPackException($"Profile '{profile.Id}' references unknown asset '{assetId}'.");
            }

            if (seen.Add(asset.Id)) selected.Add(asset);
        }

        foreach (var asset in catalog.Assets)
        {
            if (asset.Groups.Any(g => profile.Groups.Any(f => GroupMatch.Matches(f, g))) && seen.Add(asset.Id))
            {
                selected.Add(asset);
            }
        }

        return selected;
    }
}

public sealed class ProfilePlanCommand : ProfileApplyCommand
{
    protected override bool Apply => false;
}

public sealed class SourceAddCommand : Command<SourceAddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        public string Name { get; set; } = "";

        [CommandArgument(1, "<git-url>")]
        public string Url { get; set; } = "";

        [CommandOption("--branch <BRANCH>")]
        [Description("Branch to track. Default: main.")]
        public string Branch { get; set; } = "main";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        session.Sources.AddSource(settings.Name, settings.Url, settings.Branch);
        Output.Success($"Added source '{settings.Name}' ({settings.Url}, branch {settings.Branch}).");
        Output.Info("Run 'agentpack source sync' to fetch it.");
        return 0;
    }
}

public sealed class SourceListCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var config = session.Sources.LoadConfig();
        Output.Table(["Name", "Branch", "Url"], config.Sources.Select(x => new[] { x.Name, x.Branch, x.Url }));
        return 0;
    }
}

public sealed class SourceSyncCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var config = session.Sources.LoadConfig();
        if (config.Sources.Count == 0)
        {
            Output.Info("No sources registered. Add one with 'agentpack source add <name> <git-url>'.");
            return 0;
        }

        foreach (var source in config.Sources)
        {
            if (Output.CanPrompt)
            {
                AnsiConsole.Status().Start($"Syncing {source.Name}...", _ => session.Sources.Sync(source));
            }
            else
            {
                session.Sources.Sync(source);
            }

            Output.Success($"Synced {source.Name}.");
        }

        return 0;
    }
}
