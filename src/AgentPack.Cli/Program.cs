using AgentPack.Cli.Commands;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("agentpack");
    config.PropagateExceptions();

    config.AddCommand<ListCommand>("list")
        .WithDescription("List catalog assets, filterable by kind, group, and provider.")
        .WithExample("list", "skills", "--group", "backend");

    config.AddCommand<GroupsCommand>("groups")
        .WithDescription("List catalog groups.");

    config.AddCommand<NewCommand>("new")
        .WithDescription("Scaffold a new local asset (manifest + content) ready for a PR.")
        .WithExample("new", "skills", "grill-me", "--group", "review");

    config.AddCommand<ImportCommand>("import")
        .WithDescription("Scaffold an external asset pinned to an upstream commit or tag.")
        .WithExample("import", "https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d");

    config.AddCommand<AddCommand>("add")
        .WithDescription("Install assets. With no arguments, pick interactively.")
        .WithExample("add")
        .WithExample("add", "grill-me", "secret-scan", "--claude");

    config.AddCommand<RemoveCommand>("remove")
        .WithAlias("uninstall")
        .WithDescription("Remove installed assets, including their entries in shared provider configs.");

    config.AddCommand<UpgradeCommand>("upgrade")
        .WithAlias("update")
        .WithDescription("Upgrade installed assets to the catalog versions.");

    config.AddCommand<OutdatedCommand>("outdated")
        .WithDescription("Show installed assets with newer catalog versions.");

    config.AddCommand<PlanCommand>("plan")
        .WithDescription("Dry-run of add: show what would be installed where.");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show installed assets and their state.");

    config.AddCommand<DiffCommand>("diff")
        .WithDescription("Compare an installed asset against its lockfile checksum.");

    config.AddCommand<PinCommand>("pin")
        .WithDescription("Pin an installed asset so upgrades skip it.");

    config.AddCommand<UnpinCommand>("unpin")
        .WithDescription("Unpin an installed asset.");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Show environment, detected providers, and configuration.");

    config.AddBranch("catalog", catalog =>
    {
        catalog.SetDescription("Catalog maintenance (validate, lock, verify).");
        catalog.AddCommand<CatalogValidateCommand>("validate")
            .WithDescription("Validate the catalog: manifests, references, checksums.");
        catalog.AddCommand<CatalogLockCommand>("lock")
            .WithDescription("Generate catalog.lock.yaml with content checksums (run in CI).");
        catalog.AddCommand<CatalogVerifyExternalCommand>("verify-external")
            .WithDescription("Fetch every external asset at its pinned ref and verify checksums.");
    });

    config.AddBranch("profile", profile =>
    {
        profile.SetDescription("Team profiles: list and apply.");
        profile.AddCommand<ProfileListCommand>("list").WithDescription("List profiles.");
        profile.AddCommand<ProfileApplyCommand>("apply").WithDescription("Install everything a profile selects.");
        profile.AddCommand<ProfilePlanCommand>("plan").WithDescription("Dry-run of profile apply.");
    });

    config.AddBranch("source", source =>
    {
        source.SetDescription("Catalog source repositories.");
        source.AddCommand<SourceAddCommand>("add").WithDescription("Register a catalog git repository.");
        source.AddCommand<SourceListCommand>("list").WithDescription("List registered catalog sources.");
        source.AddCommand<SourceSyncCommand>("sync").WithDescription("Clone or update all registered sources.");
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
