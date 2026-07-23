using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class CatalogUseCommand : Command<CatalogUseCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<git-url>")]
        public string Url { get; set; } = "";

        [CommandOption("--name <NAME>")]
        [Description("Display name for the catalog. Default: catalog.")]
        public string Name { get; set; } = "catalog";

        [CommandOption("--branch <BRANCH>")]
        [Description("Branch to track. Default: main.")]
        public string Branch { get; set; } = "main";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();

        // Fetch before selecting: a URL or branch that cannot be reached must never
        // become the stored catalog, otherwise every later command fails.
        var candidate = SourceManager.DescribeSource(settings.Name, settings.Url, settings.Branch);
        Sync(session, candidate);

        if (!File.Exists(Path.Combine(session.Sources.SourceCachePath(candidate), "catalog.yaml")))
        {
            throw new AgentPackException(
                $"'{candidate.Url}' (branch {candidate.Branch}) has no catalog.yaml at its root.",
                "Point at a catalog repository, or rerun against the branch that holds it.");
        }

        var source = session.Sources.UseSource(candidate.Name, candidate.Url, candidate.Branch);
        Output.Success($"Using catalog '{source.Name}' ({source.Url}, branch {source.Branch}).");
        Output.Info("Catalog updates are independent from NuGet releases; install/update refresh automatically.");
        return ExitCodes.Ok;
    }

    internal static void Sync(CliSession session, AgentPackSource source)
    {
        if (Output.CanPrompt)
        {
            AnsiConsole.Status().Start($"Syncing {source.Name}...", _ => session.Sources.Sync(source));
        }
        else
        {
            session.Sources.Sync(source);
        }
    }
}

public sealed class CatalogSyncCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var source = session.Sources.RequireEffectiveSource();
        CatalogUseCommand.Sync(session, source);
        Output.Success($"Synced catalog '{source.Name}'.");
        return ExitCodes.Ok;
    }
}

public sealed class CatalogStatusCommand : Command
{
    public override int Execute(CommandContext context)
    {
        var session = new CliSession();
        var localCatalog = Path.Combine(session.Paths.WorkingDirectory, "catalog.yaml");
        if (File.Exists(localCatalog))
        {
            Output.Table(["Catalog", "Value"], new[]
            {
                new[] { "Mode", "local catalog checkout" },
                new[] { "Location", localCatalog },
                new[] { "Revision", SourceManager.HeadRevision(session.Paths.WorkingDirectory) ?? "unknown" }
            });
            Output.Info("A catalog checkout takes precedence while you are inside it.");
            return ExitCodes.Ok;
        }

        var source = session.Sources.RequireEffectiveSource();
        var cache = session.Sources.SourceCachePath(source);
        var synced = session.Sources.LastSyncedAt(source);
        Output.Table(["Catalog", "Value"], new[]
        {
            new[] { "Name", source.Name },
            new[] { "Repository", source.Url },
            new[] { "Branch", source.Branch },
            new[] { "Revision", Directory.Exists(Path.Combine(cache, ".git")) ? SourceManager.HeadRevision(cache) ?? "unknown" : "not downloaded" },
            new[] { "Last refresh", synced is null ? "never" : synced.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") },
            new[] { "Cache", cache }
        });
        Output.Info("Install and update refresh automatically; 'agentpack catalog sync' forces it now.");
        return ExitCodes.Ok;
    }
}

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

        var providers = profile.Providers.Count > 0 ? profile.Providers : settings.ResolveProviders(session.Paths, scope);
        var plan = new Installer(session.Paths).Plan(loaded, assets, providers, scope);
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
            if (asset.Groups.Any(g => profile.Groups.Contains(g, StringComparer.OrdinalIgnoreCase)) && seen.Add(asset.Id))
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
