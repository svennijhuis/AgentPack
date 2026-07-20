using AgentPack.Cli.Commands;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console;
using Spectre.Console.Cli;

// Some non-TTY hosts (containers, docker exec) report a negative console width.
// Spectre only falls back to its default when the width is exactly 0, so a negative
// width truncates every line to "…" and the CLI appears to print nothing.
if (AnsiConsole.Profile.Width <= 0)
{
    AnsiConsole.Profile.Width = 80;
}

if (args.Length == 0)
{
    GettingStarted.Show();
    return ExitCodes.Ok;
}

// Support the familiar `agentpack help [command...]` form while keeping
// Spectre.Console's generated --help output as the single source of truth.
if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
{
    args = [.. args.Skip(1), "--help"];
}

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("agentpack");
    config.SetApplicationVersion(VersionInfo.Current);
    config.PropagateExceptions();

    config.AddCommand<ListCommand>("list")
        .WithAlias("ls")
        .WithDescription("List catalog assets, filterable by kind, group, and provider.")
        .WithExample("list", "skills", "--group", "backend");

    config.AddCommand<FindCommand>("find")
        .WithAlias("search")
        .WithDescription("Search the approved catalog by id, name, description, kind, or group.")
        .WithExample("find", "typescript", "--kind", "skills");

    config.AddCommand<GroupsCommand>("groups")
        .WithDescription("List catalog groups.")
        .WithExample("groups");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Initialize a standalone or project-local asset catalog.")
        .WithExample("init")
        .WithExample("init", "--overlay");

    config.AddCommand<NewCommand>("new")
        .WithDescription("Scaffold a new local asset (manifest + content) ready for a PR.")
        .WithExample("new", "skills", "grill-me", "--group", "review");

    config.AddCommand<ImportCommand>("import")
        .WithDescription("Scaffold an external asset pinned to an upstream commit or tag.")
        .WithExample("import", "https://github.com/acme/skills/.../pdf@<commit-sha>");

    config.AddCommand<AddCommand>("add")
        .WithAlias("install")
        .WithDescription("Install assets. With no arguments, pick interactively.")
        .WithExample("add")
        .WithExample("add", "grill-me", "secret-scan", "--claude");

    config.AddCommand<RemoveCommand>("remove")
        .WithAlias("uninstall")
        .WithDescription("Remove installed assets, including their entries in shared provider configs.")
        .WithExample("remove", "grill-me", "--project");

    config.AddCommand<UpgradeCommand>("upgrade")
        .WithAlias("update")
        .WithDescription("Upgrade installed assets to the catalog versions.")
        .WithExample("upgrade", "--project");

    config.AddCommand<OutdatedCommand>("outdated")
        .WithDescription("Show installed assets with newer catalog versions.")
        .WithExample("outdated", "--project");

    config.AddCommand<PlanCommand>("plan")
        .WithDescription("Dry-run of add: show what would be installed where.")
        .WithExample("plan", "skills", "--codex", "--project");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show installed assets and their state.")
        .WithExample("status", "--project");

    config.AddCommand<DiffCommand>("diff")
        .WithDescription("Compare an installed asset against its lockfile checksum.")
        .WithExample("diff", "grill-me", "--project");

    config.AddCommand<PinCommand>("pin")
        .WithDescription("Pin an installed asset so upgrades skip it.")
        .WithExample("pin", "grill-me", "--project");

    config.AddCommand<UnpinCommand>("unpin")
        .WithDescription("Unpin an installed asset.")
        .WithExample("unpin", "grill-me", "--project");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Show environment, detected providers, catalog, and configuration.")
        .WithExample("doctor");

    config.AddBranch("catalog", catalog =>
    {
        catalog.SetDescription("Catalog maintenance (validate, lock, verify).");
        catalog.AddCommand<CatalogValidateCommand>("validate")
            .WithDescription("Validate the catalog: manifests, references, checksums.")
            .WithExample("catalog", "validate");
        catalog.AddCommand<CatalogLockCommand>("lock")
            .WithDescription("Generate catalog.lock.yaml with content checksums (run in CI).")
            .WithExample("catalog", "lock", "--check");
        catalog.AddCommand<CatalogVerifyExternalCommand>("verify-external")
            .WithDescription("Fetch every external asset at its pinned ref and verify checksums.")
            .WithExample("catalog", "verify-external");
    });

    config.AddBranch("profile", profile =>
    {
        profile.SetDescription("Team profiles: list and apply.");
        profile.AddCommand<ProfileListCommand>("list").WithDescription("List profiles.").WithExample("profile", "list");
        profile.AddCommand<ProfileApplyCommand>("apply").WithDescription("Install everything a profile selects.").WithExample("profile", "apply", "backend");
        profile.AddCommand<ProfilePlanCommand>("plan").WithDescription("Dry-run of profile apply.").WithExample("profile", "plan", "backend");
    });

    config.AddBranch("source", source =>
    {
        source.SetDescription("Catalog source repositories.");
        source.AddCommand<SourceAddCommand>("add").WithDescription("Register a catalog git repository.").WithExample("source", "add", "org", "https://github.com/acme/ai-catalog.git");
        source.AddCommand<SourceListCommand>("list").WithDescription("List registered catalog sources.").WithExample("source", "list");
        source.AddCommand<SourceSyncCommand>("sync").WithDescription("Clone or update all registered sources.").WithExample("source", "sync");
    });
});

try
{
    return app.Run(args);
}
catch (AgentPackException ex)
{
    Output.Error(ex.Message, ex.Hint);
    return ex.ExitCode;
}
catch (CommandParseException ex)
{
    Output.Error(ex.Message, Suggestions.ForParseError(ex.Message));
    AnsiConsole.MarkupLine("[grey]Run[/] [blue]agentpack --help[/] [grey]to see all commands.[/]");
    return ExitCodes.UserError;
}
catch (CommandRuntimeException ex)
{
    Output.Error(ex.Message, null);
    return ExitCodes.UserError;
}
catch (Exception ex)
{
    Output.Error("Unexpected error: " + ex.Message,
        "This looks like a bug in agentpack. Rerun with AGENTPACK_DEBUG=1 for a stack trace and report it.");
    if (Environment.GetEnvironmentVariable("AGENTPACK_DEBUG") == "1")
    {
        AnsiConsole.WriteException(ex);
    }

    return ExitCodes.Internal;
}
