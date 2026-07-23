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
        .WithDescription("List catalog assets, filterable by kind, group, and provider.")
        .WithExample("list", "skills", "--group", "backend");

    config.AddCommand<SearchCommand>("search")
        .WithDescription("Search the approved catalog by id, name, description, kind, or group.")
        .WithExample("search", "typescript", "--kind", "skills");

    config.AddCommand<GroupsCommand>("groups")
        .WithDescription("List catalog groups.")
        .WithExample("groups");

    config.AddCommand<SubmitCommand>("submit")
        .WithDescription("Propose a local or external asset to the catalog through a pull request.")
        .WithExample("submit", "skill", "./my-skill")
        .WithExample("submit", "hook", "./check.sh", "--trigger", "preToolUse")
        .WithExample("submit", "mcp", "github", "--command", "github-mcp-server", "--env", "GITHUB_TOKEN")
        .WithExample("submit", "skill", "https://github.com/acme/skills/tree/main/my-skill")
        .WithExample("submit", "skill", "./my-skill", "--update");

    config.AddCommand<InstallCommand>("install")
        .WithDescription("Install catalog assets into your user profile or current project.")
        .WithExample("install")
        .WithExample("install", "grill-me", "--codex", "--user");

    config.AddCommand<RemoveCommand>("remove")
        .WithDescription("Remove installed assets, including their entries in shared provider configs.")
        .WithExample("remove", "grill-me", "--project");

    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Update installed assets to the catalog versions.")
        .WithExample("update", "--project");

    config.AddCommand<OutdatedCommand>("outdated")
        .WithDescription("Show installed assets with newer catalog versions.")
        .WithExample("outdated", "--project");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show installed assets and their state.")
        .WithExample("status", "--project");

    config.AddCommand<DiffCommand>("diff")
        .WithDescription("Compare an installed asset against its lockfile checksum.")
        .WithExample("diff", "grill-me", "--project");

    config.AddCommand<PinCommand>("pin")
        .WithDescription("Pin an installed asset so updates skip it.")
        .WithExample("pin", "grill-me", "--project");

    config.AddCommand<UnpinCommand>("unpin")
        .WithDescription("Unpin an installed asset.")
        .WithExample("unpin", "grill-me", "--project");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Show environment, detected providers, catalog, and configuration.")
        .WithExample("doctor");

    config.AddBranch("catalog", catalog =>
    {
        catalog.SetDescription("Select, inspect, sync, and maintain the asset catalog.");
        catalog.AddCommand<CatalogUseCommand>("use")
            .WithDescription("Select and immediately sync the active catalog repository.")
            .WithExample("catalog", "use", "https://github.com/acme/ai-catalog.git");
        catalog.AddCommand<CatalogStatusCommand>("status")
            .WithDescription("Show the active catalog, revision, cache, and last refresh.")
            .WithExample("catalog", "status");
        catalog.AddCommand<CatalogSyncCommand>("sync")
            .WithDescription("Immediately refresh the active catalog.")
            .WithExample("catalog", "sync");
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
