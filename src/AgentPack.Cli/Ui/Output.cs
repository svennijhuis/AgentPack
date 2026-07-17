using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

/// <summary>Console rendering. Everything degrades to plain text when output is redirected.</summary>
public static class Output
{
    /// <summary>True when prompts can be shown: a real terminal and not CI.</summary>
    public static bool CanPrompt =>
        AnsiConsole.Profile.Capabilities.Interactive &&
        !Console.IsInputRedirected &&
        Environment.GetEnvironmentVariable("CI") is null;

    public static void Error(string message, string? hint)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            AnsiConsole.MarkupLine($"[yellow]hint:[/] {Markup.Escape(hint)}");
        }
    }

    public static void Warning(string message) =>
        AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");

    public static void Success(string message) =>
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");

    public static void Info(string message) =>
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(header)}[/]"));
        }

        var any = false;
        foreach (var row in rows)
        {
            any = true;
            table.AddRow(row.Select(cell => Markup.Escape(cell ?? "")).ToArray());
        }

        if (!any)
        {
            Info("(nothing to show)");
            return;
        }

        AnsiConsole.Write(table);
    }

    public static void Plan(string title, InstallPlan plan, InstallScope scope, string workingDirectory, string providerHome)
    {
        var scopeName = scope == InstallScope.User ? "user" : "project";
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/] [grey]({scopeName} scope)[/]");

        if (plan.Items.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("[bold]ID[/]");
            table.AddColumn("[bold]Kind[/]");
            table.AddColumn("[bold]Provider[/]");
            table.AddColumn("[bold]Version[/]");
            table.AddColumn("[bold]Action[/]");
            table.AddColumn("[bold]Target[/]");

            var root = scope == InstallScope.User ? providerHome : workingDirectory;
            foreach (var item in plan.Items)
            {
                var action = item.State switch
                {
                    InstallState.Available => item.Target.Mode.OwnsWholeTarget() ? "[green]install[/]" : "[green]merge into[/]",
                    InstallState.UpdateAvailable => "[blue]update[/]",
                    InstallState.Installed => "[grey]up to date[/]",
                    InstallState.Pinned => "[grey]pinned (skip)[/]",
                    InstallState.LocalChanges => "[yellow]local changes[/]",
                    InstallState.Missing => "[yellow]reinstall (missing)[/]",
                    InstallState.UnmanagedPresent => "[yellow]overwrite unmanaged[/]",
                    _ => Markup.Escape(item.State.Display())
                };

                var target = Path.GetRelativePath(root, item.TargetPath);
                if (target.StartsWith("..")) target = item.TargetPath;
                table.AddRow(
                    Markup.Escape(item.Asset.Id),
                    Markup.Escape(item.Asset.Kind.Display()),
                    Markup.Escape(item.Provider.Display()),
                    Markup.Escape(item.Asset.Version.ToString()),
                    action,
                    Markup.Escape(target));
            }

            AnsiConsole.Write(table);
        }
        else
        {
            Info("Nothing to do.");
        }

        foreach (var skip in plan.Skipped)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]skipped[/] {Markup.Escape(skip.Asset.Id)} [grey]for[/] {Markup.Escape(skip.Provider.Display())}[grey]:[/] [grey]{Markup.Escape(skip.Reason)}[/]");
        }
    }

    public static void ApplyResults(IReadOnlyList<ApplyResult> results)
    {
        foreach (var result in results)
        {
            var label = $"{result.Item.Asset.Id} ({result.Item.Provider.Display()})";
            switch (result.Outcome)
            {
                case ApplyOutcome.Installed:
                    Success($"installed {label} {result.Item.Asset.Version}");
                    break;
                case ApplyOutcome.Updated:
                    Success($"updated {label} to {result.Item.Asset.Version}");
                    break;
                case ApplyOutcome.AlreadyUpToDate:
                    Info($"  {label} already up to date");
                    break;
                case ApplyOutcome.KeptLocalChanges:
                    Warning($"kept local changes for {label} (catalog version not applied)");
                    break;
                case ApplyOutcome.SkippedPinned:
                    Info($"  {label} pinned — skipped (use 'agentpack unpin {result.Item.Asset.Id}' to allow updates)");
                    break;
            }
        }
    }

    public static void Report(ValidationReport report)
    {
        if (report.Issues.Count == 0)
        {
            Success("No issues found.");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[bold]Severity[/]");
        table.AddColumn("[bold]Code[/]");
        table.AddColumn("[bold]Message[/]");
        foreach (var issue in report.Issues.OrderBy(x => x.Severity == IssueSeverity.Error ? 0 : 1))
        {
            var severity = issue.Severity == IssueSeverity.Error ? "[red]error[/]" : "[yellow]warning[/]";
            table.AddRow(severity, Markup.Escape(issue.Code), Markup.Escape(issue.Message));
        }

        AnsiConsole.Write(table);
    }

    public static void Warnings(IReadOnlyList<CatalogIssue> warnings)
    {
        foreach (var warning in warnings)
        {
            Warning(warning.Message);
        }
    }
}
