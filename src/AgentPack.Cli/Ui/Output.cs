using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

/// <summary>Console rendering. Everything degrades to plain text when output is redirected.</summary>
public static class Output
{
    /// <summary>Rows with these states carry no work; they collapse into a summary line when numerous.</summary>
    private const int QuietRowCollapseThreshold = 5;

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

    /// <summary>Shortens text to fit a column; the full value stays available via narrower filters.</summary>
    public static string Fit(string text, int max) =>
        max > 3 && text.Length > max ? text[..(max - 3)] + "..." : text;

    /// <summary>
    /// Renders a table. Cells are escaped except in <paramref name="markupColumns"/>,
    /// whose cells the caller styles (and escapes) itself.
    /// </summary>
    public static void Table(string[] headers, IEnumerable<string[]> rows, string? emptyMessage = null, int[]? markupColumns = null)
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
            table.AddRow(row.Select((cell, i) =>
                markupColumns is not null && markupColumns.Contains(i) ? cell ?? "" : Markup.Escape(cell ?? "")).ToArray());
        }

        if (!any)
        {
            Info(emptyMessage ?? "(nothing to show)");
            return;
        }

        AnsiConsole.Write(table);
    }

    public static void Plan(string title, InstallPlan plan, InstallScope scope, string workingDirectory, string providerHome)
    {
        var scopeName = scope == InstallScope.User ? "user" : "project";
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/] [grey]({scopeName} scope)[/]");

        if (plan.Items.Count == 0)
        {
            Info("Nothing to do.");
        }
        else
        {
            // Rows that need no work only add noise in bulk; fold them into one line.
            var quiet = plan.Items.Where(x => x.State is InstallState.Installed or InstallState.Pinned).ToList();
            var collapse = quiet.Count > QuietRowCollapseThreshold;
            var shown = collapse ? plan.Items.Where(x => !quiet.Contains(x)).ToList() : plan.Items.ToList();

            if (shown.Count > 0)
            {
                var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
                table.AddColumn("[bold]ID[/]");
                table.AddColumn("[bold]Kind[/]");
                table.AddColumn("[bold]Provider[/]");
                table.AddColumn("[bold]Version[/]");
                table.AddColumn("[bold]Action[/]");
                table.AddColumn("[bold]Target[/]");

                var root = scope == InstallScope.User ? providerHome : workingDirectory;
                foreach (var item in shown)
                {
                    var action = item.State switch
                    {
                        InstallState.Available => item.Target.Mode == InstallMode.CopyTree ? "[green]install[/]" : "[green]merge into[/]",
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

            if (collapse)
            {
                var upToDate = quiet.Count(x => x.State == InstallState.Installed);
                var parts = new List<string>();
                if (upToDate > 0) parts.Add($"{upToDate} already up to date");
                if (quiet.Count > upToDate) parts.Add($"{quiet.Count - upToDate} pinned");
                Info($"{string.Join(", ", parts)} — not shown ('agentpack status' lists everything).");
            }

            var actionable = plan.Items.Where(x => x.State is not (InstallState.Installed or InstallState.Pinned)).ToList();
            if (actionable.Count == 0)
            {
                Success("Everything is already up to date.");
            }
            else if (collapse || actionable.Count > QuietRowCollapseThreshold)
            {
                Info("To do: " + string.Join(", ", actionable
                    .GroupBy(x => x.State)
                    .OrderBy(g => g.Key)
                    .Select(g => $"{g.Count()} {ActionWord(g.Key)}")) + ".");
            }
        }

        // One line per distinct reason keeps a many-provider plan readable.
        foreach (var group in plan.Skipped.GroupBy(x => (Provider: x.Provider.Display(), x.Reason)))
        {
            var ids = group.Select(x => x.Asset.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var subject = ids.Count == 1 ? ids[0] : $"{ids.Count} assets ({string.Join(", ", ids)})";
            AnsiConsole.MarkupLine(
                $"  [grey]skipped[/] {Markup.Escape(subject)} [grey]for[/] {Markup.Escape(group.Key.Provider)}[grey]:[/] [grey]{Markup.Escape(group.Key.Reason)}[/]");
        }
    }

    public static void ApplyResults(IReadOnlyList<ApplyResult> results)
    {
        var upToDate = results.Where(x => x.Outcome == ApplyOutcome.AlreadyUpToDate).ToList();
        var collapseUpToDate = upToDate.Count > QuietRowCollapseThreshold;
        var pinned = results.Where(x => x.Outcome == ApplyOutcome.SkippedPinned).ToList();
        var collapsePinned = pinned.Count > QuietRowCollapseThreshold;

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
                case ApplyOutcome.AlreadyUpToDate when !collapseUpToDate:
                    Info($"  {label} already up to date");
                    break;
                case ApplyOutcome.KeptLocalChanges:
                    Warning($"kept local changes for {label} (catalog version not applied)");
                    break;
                case ApplyOutcome.SkippedPinned when !collapsePinned:
                    Info($"  {label} pinned — skipped (use 'agentpack unpin {result.Item.Asset.Id}' to allow updates)");
                    break;
            }
        }

        if (collapseUpToDate)
        {
            Info($"  {upToDate.Count} already up to date");
        }

        if (collapsePinned)
        {
            Info($"  {pinned.Count} pinned — skipped ('agentpack unpin <id>' allows updates)");
        }

        var installed = results.Count(x => x.Outcome == ApplyOutcome.Installed);
        var updated = results.Count(x => x.Outcome == ApplyOutcome.Updated);
        var kept = results.Count(x => x.Outcome == ApplyOutcome.KeptLocalChanges);
        if (results.Count > QuietRowCollapseThreshold && installed + updated + kept > 0)
        {
            var parts = new List<string>();
            if (installed > 0) parts.Add($"{installed} installed");
            if (updated > 0) parts.Add($"{updated} updated");
            if (kept > 0) parts.Add($"{kept} kept local");
            Success($"Done: {string.Join(", ", parts)}.");
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

    private static string ActionWord(InstallState state) => state switch
    {
        InstallState.Available => "install",
        InstallState.UpdateAvailable => "update",
        InstallState.LocalChanges => "with local changes",
        InstallState.Missing => "reinstall",
        InstallState.UnmanagedPresent => "overwrite unmanaged",
        _ => state.Display()
    };
}
