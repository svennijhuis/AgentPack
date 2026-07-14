using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

/// <summary>Console rendering. Everything degrades to plain text when output is redirected.</summary>
public static class Output
{
    private static bool Plain => Console.IsOutputRedirected;

    /// <summary>True when prompts can be shown: a real terminal and not CI.</summary>
    public static bool CanPrompt =>
        AnsiConsole.Profile.Capabilities.Interactive &&
        !Console.IsInputRedirected &&
        Environment.GetEnvironmentVariable("CI") is null;

    public static void Error(string message, string? hint)
    {
        if (Plain)
        {
            Console.WriteLine($"error: {message}");
            if (!string.IsNullOrWhiteSpace(hint)) Console.WriteLine($"hint: {hint}");
            return;
        }
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");
        if (!string.IsNullOrWhiteSpace(hint))
        {
            AnsiConsole.MarkupLine($"[yellow]hint:[/] {Markup.Escape(hint)}");
        }
    }

    public static void Warning(string message)
    {
        if (Plain) Console.WriteLine($"! {message}");
        else AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(message)}");
    }

    public static void Success(string message)
    {
        if (Plain) Console.WriteLine($"OK {message}");
        else AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }

    public static void Info(string message)
    {
        if (Plain) Console.WriteLine(message);
        else AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        if (Plain)
        {
            var materialized = rows.ToList();
            if (materialized.Count == 0)
            {
                Info("(nothing to show)");
                return;
            }
            Console.WriteLine(string.Join("\t", headers));
            foreach (var row in materialized)
                Console.WriteLine(string.Join("\t", row.Select(cell => cell ?? "")));
            return;
        }

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
                    InstallState.Available => item.Target.Mode == InstallMode.CopyTree ? "[green]install[/]" : "[green]merge into[/]",
                    InstallState.UpdateAvailable => "[blue]update[/]",
                    InstallState.Installed => "[grey]up to date[/]",
                    InstallState.Pinned => "[grey]pinned (skip)[/]",
                    InstallState.LocalChanges when item.RenderFingerprint is not null &&
                        !item.RenderFingerprint.Equals(item.Existing?.RenderFingerprint, StringComparison.OrdinalIgnoreCase) =>
                        "[yellow]local changes; rebuild required[/]",
                    InstallState.LocalChanges => "[yellow]local changes[/]",
                    InstallState.Missing => "[yellow]reinstall (missing)[/]",
                    InstallState.UnmanagedPresent => "[yellow]overwrite unmanaged[/]",
                    InstallState.Adoptable => "[green]adopt identical[/]",
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

    public static void AgentCompatibility(
        LoadedCatalog loaded,
        IEnumerable<(Asset Agent, ProviderName Provider)> selections)
    {
        var rows = selections
            .Where(x => x.Agent.Kind == AssetKind.Agents)
            .DistinctBy(x => (x.Agent.Id, x.Provider))
            .OrderBy(x => x.Agent.Id, StringComparer.Ordinal)
            .ThenBy(x => x.Provider)
            .Select(x =>
            {
                var dependencies = new AgentDependencyResolver(loaded.Catalog).Resolve(x.Agent, x.Provider);
                return (x.Agent, Projection: AgentPack.Core.AgentCompatibility.Project(x.Agent, x.Provider, dependencies));
            })
            .ToList();

        RenderAgentCompatibility(rows);
    }

    /// <summary>
    /// Shows the declared licenses for external content involved in an add,
    /// including dependencies that are embedded into or installed for agents.
    /// This is a notice only: AgentPack does not claim to have verified legal compliance.
    /// </summary>
    public static void ExternalLicenseNotices(LoadedCatalog loaded, InstallPlan plan)
    {
        var assets = plan.Items.Select(x => x.Asset).ToList();
        var resolver = new AgentDependencyResolver(loaded.Catalog);
        foreach (var item in plan.Items.Where(x => x.Asset.Kind == AssetKind.Agents))
        {
            var dependencies = resolver.Resolve(item.Asset, item.Provider);
            assets.AddRange(dependencies.Instructions);
            assets.AddRange(dependencies.Skills);
            assets.AddRange(dependencies.Mcp);
        }

        var externalAssets = assets
            .Where(x => x.Source is AssetSource.External)
            .DistinctBy(x => (x.Kind, x.Id))
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Kind)
            .ToList();
        if (externalAssets.Count == 0) return;

        AnsiConsole.MarkupLine("[bold]Third-party license notice[/]");
        Table(
            ["Asset", "Kind", "Declared license", "Source"],
            externalAssets.Select(asset =>
            {
                var source = (AssetSource.External)asset.Source;
                var sourceDisplay = $"{source.Url}@{source.Ref}";
                if (!string.IsNullOrWhiteSpace(source.Path)) sourceDisplay += $" ({source.Path})";
                return new[]
                {
                    asset.Id,
                    asset.Kind.Display(),
                    string.IsNullOrWhiteSpace(source.License) ? "not recorded" : source.License,
                    sourceDisplay
                };
            }));

        foreach (var asset in externalAssets.Where(x =>
                     string.IsNullOrWhiteSpace(((AssetSource.External)x.Source).License)))
        {
            Warning($"No license is recorded for external asset '{asset.Id}'. " +
                    "Review the upstream source before installing or redistributing it.");
        }

        Info("These assets contain third-party content. Review and comply with each upstream license before using or redistributing it.");
    }

    public static void AgentCompatibility(Asset agent, IReadOnlyList<ProviderName> providers)
    {
        var emptyDependencies = new ResolvedAgentDependencies([], [], []);
        var rows = providers
            .Select(provider => (Agent: agent, Projection: AgentPack.Core.AgentCompatibility.Project(
                agent, provider, emptyDependencies)))
            .ToList();
        RenderAgentCompatibility(rows);
    }

    public static void ExternalAgentInspection(ExternalAgentInspection inspection)
    {
        AnsiConsole.MarkupLine("[bold]Detected upstream agent frontmatter[/] [grey](suggestions only; not trusted)[/]");
        if (inspection.Name is not null) Info($"Upstream name: {inspection.Name}");
        if (inspection.Description is not null) Info($"Upstream description: {inspection.Description}");
        if (inspection.ToolMappings.Count > 0)
        {
            Table(
                ["Upstream tool", "Portable suggestion", "Import behavior"],
                inspection.ToolMappings.Select(x => new[]
                {
                    x.NativeTool,
                    x.PortableTools.Count == 0
                        ? "not mapped"
                        : string.Join(", ", x.PortableTools.Select(t => t.ToString().ToLowerInvariant())),
                    x.PortableTools.Count == 0 ? "requires MCP or manual choice" : "author must approve"
                }));
        }

        if (inspection.Model is not null)
            Warning($"Upstream model '{inspection.Model}' is ignored. AgentPack strips model metadata so the user, session, or workflow keeps its current model.");
        if (inspection.UnknownTools.Count > 0)
            Warning("Unrecognized upstream tools will not be imported: " + string.Join(", ", inspection.UnknownTools) +
                    ". Package custom tools as MCP or choose an intentional portable capability.");
    }

    private static void RenderAgentCompatibility(
        IReadOnlyList<(Asset Agent, AgentProviderProjection Projection)> rows)
    {

        if (rows.Count == 0) return;

        AnsiConsole.MarkupLine("[bold]Agent provider compatibility[/]");
        foreach (var group in rows.GroupBy(x => x.Agent.Id, StringComparer.OrdinalIgnoreCase))
        {
            var agent = group.First().Agent;
            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(agent.Id)}[/]");
            Table(
                ["Provider", "Native tools", "Enforcement", "Model"],
                group.Select(x => new[]
                {
                    x.Projection.Provider.Display(),
                    x.Projection.NativeTools,
                    x.Projection.Enforcement,
                    x.Projection.Model
                }));
            foreach (var row in group)
                Info($"  {row.Projection.Provider.Display()}: {row.Projection.Note}");

            var coarseWritable = group.Any(x => x.Projection.Enforcement == "coarse" &&
                (agent.Agent?.Tools?.Contains(AgentTool.Edit) == true ||
                 agent.Agent?.Tools?.Contains(AgentTool.Execute) == true));
            if (coarseWritable)
            {
                Warning($"{agent.Id}: Codex/Cursor cannot mechanically enforce this granular list while edit/execute is enabled. " +
                        "For a reviewer that must be read-only everywhere, use a project overlay with tools: [read, search].");
            }

            Warning($"{agent.Id}: model fields are omitted for every provider. " +
                    "The generated agent uses the user's, session's, or workflow's current/default model.");
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
                case ApplyOutcome.SkippedTransaction:
                    Warning($"skipped {label} because its agent transaction was kept local");
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
