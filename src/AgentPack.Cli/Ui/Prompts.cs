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

        if (item.SourcePath is null)
        {
            Output.Info("Incoming content is external and not fetched yet; no diff available until apply.");
            return;
        }

        var incoming = FileSet(item.SourcePath);
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
