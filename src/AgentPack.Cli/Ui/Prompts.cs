using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

public static class Prompts
{
    /// <summary>
    /// Interactive checklist of assets grouped by kind. Used by 'add' (nothing preselected)
    /// and 'upgrade' (outdated entries preselected).
    /// </summary>
    public static IReadOnlyList<Asset> SelectAssets(IReadOnlyList<Asset> assets, string title, bool preselectAll)
    {
        var prompt = new MultiSelectionPrompt<AssetChoice>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(18)
            .NotRequired()
            .InstructionsText("[grey](space toggles, enter confirms, arrows move)[/]")
            .UseConverter(choice => choice.Label);

        foreach (var kindGroup in assets.GroupBy(x => x.Kind).OrderBy(x => x.Key))
        {
            var header = new AssetChoice($"[bold]{kindGroup.Key.Display()}[/]", null);
            var children = kindGroup
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(asset => new AssetChoice(Label(asset), asset))
                .ToList();
            prompt.AddChoiceGroup(header, children);
            if (preselectAll)
            {
                foreach (var child in children) prompt.Select(child);
            }
        }

        return AnsiConsole.Prompt(prompt).Where(x => x.Asset is not null).Select(x => x.Asset!).ToList();
    }

    public static bool Confirm(string question) => AnsiConsole.Confirm(question, defaultValue: true);

    public static string Text(string question, string defaultValue) => AnsiConsole.Prompt(
        new TextPrompt<string>($"[bold]{Markup.Escape(question)}[/]")
            .DefaultValue(defaultValue)
            .Validate(input => string.IsNullOrWhiteSpace(input)
                ? ValidationResult.Error("[red]A value is required.[/]")
                : ValidationResult.Success())).Trim();

    public static Asset SelectAgent(IReadOnlyList<Asset> agents, string title)
    {
        var choices = agents.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        return AnsiConsole.Prompt(new SelectionPrompt<Asset>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(18)
            .MoreChoicesText("[grey](move up and down to reveal more agents)[/]")
            .UseConverter(Label)
            .AddChoices(choices));
    }

    public static IReadOnlyList<ProviderName> SelectAgentProviders(IReadOnlyList<ProviderName>? selected = null)
    {
        var choices = ProviderNames.All.ToList();
        var prompt = new MultiSelectionPrompt<ProviderName>()
            .Title("[bold]Which providers should this agent support?[/]")
            .PageSize(8)
            .InstructionsText("[grey](space toggles, enter confirms)[/]")
            .UseConverter(x => x.Display())
            .AddChoices(choices);
        foreach (var provider in selected ?? choices) prompt.Select(provider);
        return AnsiConsole.Prompt(prompt);
    }

    public static IReadOnlyList<AgentTool> SelectAgentTools(IReadOnlyList<AgentTool>? suggested = null)
    {
        var mode = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("[bold]How should built-in tools be configured?[/]")
            .AddChoices(
                "inherit all tools from each provider (recommended)",
                "choose portable capability classes"));
        if (mode.StartsWith("inherit", StringComparison.Ordinal)) return [];

        var choices = Enum.GetValues<AgentTool>().ToList();
        var prompt = new MultiSelectionPrompt<AgentTool>()
            .Title("[bold]Select portable capabilities[/]")
            .PageSize(10)
            .InstructionsText("[grey](space toggles, enter confirms; at least one required)[/]")
            .UseConverter(x => x.ToString().ToLowerInvariant())
            .AddChoices(choices);
        foreach (var tool in suggested ?? [AgentTool.Read, AgentTool.Search]) prompt.Select(tool);
        return AnsiConsole.Prompt(prompt);
    }

    /// <summary>
    /// Asks what to do with an item whose installed content was modified locally.
    /// Offers a diff so the user can decide with full information.
    /// </summary>
    public static DriftAction ResolveDrift(InstallPlanItem item, InstallScope scope, string scopeRoot)
    {
        var keepLabel = item.State == InstallState.UnmanagedPresent ? "keep the existing file" : "keep my local changes";
        Output.Warning(item.State == InstallState.UnmanagedPresent
            ? $"{item.Asset.Id} ({item.Provider.Display()}): the target already exists and was not installed by agentpack."
            : $"{item.Asset.Id} ({item.Provider.Display()}) was modified locally after install.");

        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"Overwrite [bold]{Markup.Escape(item.Asset.Id)}[/] with catalog version {item.Asset.Version}?")
                .AddChoices("overwrite with catalog version", keepLabel, "show diff", "abort"));

            switch (choice)
            {
                case "overwrite with catalog version":
                    return DriftAction.Overwrite;
                case var _ when choice == keepLabel:
                    return DriftAction.Keep;
                case "abort":
                    throw new AgentPackException("Aborted by user.", exitCode: ExitCodes.DriftOrConflict);
                default:
                    ShowDiff(item, scope, scopeRoot);
                    break;
            }
        }
    }

    /// <summary>File-level diff between the installed content and the incoming catalog content or merge fragment.</summary>
    public static void ShowDiff(InstallPlanItem item, InstallScope scope, string scopeRoot)
    {
        if (item.Target.Mode is InstallMode.CopyTree or InstallMode.RenderAgent)
        {
            var candidate = item.StagedCandidatePath ?? item.SourcePath;
            var snapshots = new List<string[]>
            {
                new[] { "last managed", item.Existing?.Version ?? "(none)", Hash(item.Existing?.ManagedSnapshotPath) },
                new[] { "local", "current", Hash(item.TargetPath) },
                new[] { "catalog candidate", item.Asset.Version.ToString(), Hash(candidate) }
            };
            Output.Table(["Version", "Label", "Checksum"], snapshots.Select(x => new[] { x[1], x[0], x[2] }));
        }

        switch (item.Target.Mode)
        {
            case InstallMode.MergeMcp:
                AnsiConsole.MarkupLine("[grey]Fragment that will be merged into[/] " + Markup.Escape(item.TargetPath) + "[grey]:[/]");
                AnsiConsole.WriteLine(McpMerger.Preview(item.Asset, item.SourcePath, item.Target, scope));
                return;

            case InstallMode.MergeHook:
                AnsiConsole.MarkupLine("[grey]Fragment that will be merged into[/] " + Markup.Escape(item.TargetPath) + "[grey]:[/]");
                AnsiConsole.WriteLine(HookMerger.Preview(item.Asset, item.Target, scopeRoot));
                return;
        }

        var incomingPath = item.StagedCandidatePath ?? item.SourcePath;
        if (incomingPath is null)
        {
            Output.Info("Incoming content is external and not fetched yet; no diff available until apply.");
            return;
        }

        var incoming = FileSet(incomingPath);
        var installed = FileSet(item.TargetPath);
        var all = incoming.Keys.Union(installed.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("[bold]File[/]");
        table.AddColumn("[bold]Change if overwritten[/]");
        foreach (var file in all)
        {
            var inIncoming = incoming.ContainsKey(file);
            var inInstalled = installed.ContainsKey(file);
            var change = (inIncoming, inInstalled) switch
            {
                (true, false) => "[green]added[/]",
                (false, true) => "[red]removed[/]",
                _ => incoming[file] == installed[file] ? "[grey]unchanged[/]" : "[yellow]replaced[/]"
            };
            table.AddRow(Markup.Escape(file), change);
        }

        AnsiConsole.Write(table);

        static string Hash(string? path) => !string.IsNullOrWhiteSpace(path) &&
            (File.Exists(path) || Directory.Exists(path)) ? ContentHash.Compute(path) : "(unavailable)";
    }

    private static Dictionary<string, string> FileSet(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            result[Path.GetFileName(path)] = ContentHash.Compute(path);
        }
        else if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                result[Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/')] = ContentHash.Compute(file);
            }
        }

        return result;
    }

    private static string Label(Asset asset)
    {
        var description = asset.Description.Length > 60 ? asset.Description[..57] + "..." : asset.Description;
        var badge = asset.Status switch
        {
            AssetStatus.Experimental => " [grey](experimental)[/]",
            AssetStatus.Deprecated => " [yellow](deprecated)[/]",
            _ => ""
        };
        return $"{Markup.Escape(asset.Id)} [grey]{asset.Version}[/]  {Markup.Escape(description)}{badge}";
    }

    private sealed record AssetChoice(string Label, Asset? Asset);
}
