using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

/// <summary>Whether an asset offered in the picker is already installed for the active scope.</summary>
public enum AssetInstallMarker
{
    Installed,
    UpdateAvailable
}

public static class Prompts
{
    /// <summary>
    /// Interactive asset picker. Used by 'install' (nothing preselected) and 'update'
    /// (everything preselected). Catalogs that fit on one page get a single grouped
    /// checklist; larger ones get a category browser so nobody scrolls through
    /// hundreds of rows: pick a kind, tick assets, repeat, then Done.
    /// <paramref name="installed"/> annotates rows already present in the target scope so
    /// the picker shows what will be installed fresh versus updated in place.
    /// </summary>
    public static IReadOnlyList<Asset> SelectAssets(
        IReadOnlyList<Asset> assets,
        string title,
        bool preselectAll,
        IReadOnlyDictionary<string, AssetInstallMarker>? installed = null)
    {
        var pageSize = PageSize();
        var kinds = assets.Select(x => x.Kind).Distinct().Count();

        // +kinds: group headers occupy rows too.
        if (assets.Count + kinds <= pageSize || kinds == 1)
        {
            var preselected = preselectAll ? assets.Select(x => x.Id) : [];
            return Checklist(assets, $"{title} — {assets.Count} available", preselected.ToHashSet(StringComparer.OrdinalIgnoreCase), pageSize, installed);
        }

        return BrowseByKind(assets, title, preselectAll, pageSize, installed);
    }

    /// <summary>Category loop with a cart: kinds carry counts, selections survive switching kinds.</summary>
    private static IReadOnlyList<Asset> BrowseByKind(IReadOnlyList<Asset> assets, string title, bool preselectAll, int pageSize, IReadOnlyDictionary<string, AssetInstallMarker>? installed)
    {
        var cart = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        if (preselectAll)
        {
            foreach (var asset in assets) cart[asset.Id] = asset;
        }

        var kinds = assets.GroupBy(x => x.Kind).OrderBy(x => x.Key).ToList();
        while (true)
        {
            var done = new KindChoice($"[green]Done[/] — {(cart.Count == 0 ? "nothing selected" : $"{cart.Count} selected")}", Done: true);
            var everything = new KindChoice(KindLabel("everything", assets, cart), Kind: null);
            var search = new KindChoice("find one asset [grey](type its name)[/]", Search: true);
            var prompt = new SelectionPrompt<KindChoice>()
                .Title($"[bold]{Markup.Escape(title)}[/] [grey]({assets.Count} assets — pick a category, tick assets, repeat; type to search)[/]")
                .PageSize(Math.Max(pageSize, kinds.Count + 4))
                .EnableSearch()
                .UseConverter(choice => choice.Label);
            prompt.AddChoices(kinds
                .Select(g => new KindChoice(KindLabel(g.Key.Display(), g.ToList(), cart), g.Key))
                .Prepend(search)
                .Prepend(everything)
                .Prepend(done)
                .Append(new KindChoice("[grey]Cancel[/]", Cancel: true)));

            var choice = AnsiConsole.Prompt(prompt);
            if (choice.Done) break;
            if (choice.Cancel) return [];
            if (choice.Search)
            {
                SearchAssets(assets, cart, pageSize, installed);
                continue;
            }

            var subset = choice.Kind is { } kind ? kinds.First(g => g.Key == kind).ToList() : assets.ToList();
            var scope = choice.Kind is { } k ? k.Display() : "everything";
            var picked = Checklist(
                subset,
                $"{title}: {scope} — {subset.Count} available",
                subset.Where(x => cart.ContainsKey(x.Id)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                pageSize,
                installed);

            // The checklist result is authoritative for what it showed: unticked means removed.
            foreach (var asset in subset) cart.Remove(asset.Id);
            foreach (var asset in picked) cart[asset.Id] = asset;
        }

        return cart.Values.OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Type-to-find across every asset regardless of kind; picking one toggles it
    /// in the cart. Loops so several assets can be found in a row.
    /// </summary>
    private static void SearchAssets(IReadOnlyList<Asset> assets, Dictionary<string, Asset> cart, int pageSize, IReadOnlyDictionary<string, AssetInstallMarker>? installed)
    {
        while (true)
        {
            var prompt = new SelectionPrompt<AssetChoice>()
                .Title($"[bold]Find an asset[/] [grey](type to filter, enter toggles it, {cart.Count} selected)[/]")
                .PageSize(pageSize)
                .EnableSearch()
                .UseConverter(choice => choice.Label);
            prompt.AddChoices(assets
                .OrderBy(x => x.Kind).ThenBy(x => x.Id, StringComparer.Ordinal)
                .Select(asset => new AssetChoice(
                    $"{(cart.ContainsKey(asset.Id) ? "[green]✓[/] " : "  ")}{Label(asset, installed)} [grey]· {asset.Kind.Display()}[/]",
                    asset))
                .Prepend(new AssetChoice("[grey]back to categories[/]", null)));

            var picked = AnsiConsole.Prompt(prompt).Asset;
            if (picked is null) return;

            if (!cart.Remove(picked.Id))
            {
                cart[picked.Id] = picked;
                Output.Info($"+ {picked.Id} added ({cart.Count} selected)");
            }
            else
            {
                Output.Info($"- {picked.Id} removed ({cart.Count} selected)");
            }
        }
    }

    private static IReadOnlyList<Asset> Checklist(IReadOnlyList<Asset> assets, string title, HashSet<string> preselectedIds, int pageSize, IReadOnlyDictionary<string, AssetInstallMarker>? installed)
    {
        var prompt = new MultiSelectionPrompt<AssetChoice>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(pageSize)
            .NotRequired()
            .InstructionsText("[grey](space toggles, enter confirms, arrows move — a kind row toggles its whole group)[/]")
            .UseConverter(choice => choice.Label);

        foreach (var kindGroup in assets.GroupBy(x => x.Kind).OrderBy(x => x.Key))
        {
            var header = new AssetChoice($"[bold]{kindGroup.Key.Display()}[/]", null);
            var children = kindGroup
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(asset => new AssetChoice(Label(asset, installed), asset))
                .ToList();
            prompt.AddChoiceGroup(header, children);
            foreach (var child in children.Where(x => preselectedIds.Contains(x.Asset!.Id)))
            {
                prompt.Select(child);
            }
        }

        return AnsiConsole.Prompt(prompt).Where(x => x.Asset is not null).Select(x => x.Asset!).ToList();
    }

    private static string KindLabel(string name, IReadOnlyList<Asset> subset, Dictionary<string, Asset> cart)
    {
        var selected = subset.Count(x => cart.ContainsKey(x.Id));
        return selected > 0
            ? $"{Markup.Escape(name)} [grey]({selected} of {subset.Count} selected)[/]"
            : $"{Markup.Escape(name)} [grey]({subset.Count})[/]";
    }

    private static int PageSize() => Math.Clamp(AnsiConsole.Profile.Height - 6, 10, 30);

    public static bool Confirm(string question) => AnsiConsole.Confirm(question, defaultValue: true);

    /// <summary>Publishing affects a shared repository, so Enter alone must not approve it.</summary>
    public static bool ConfirmSubmission(string question) => AnsiConsole.Confirm(question, defaultValue: false);

    public static bool ConfirmApply(IReadOnlyList<InstallPlanItem> items)
    {
        var assets = items
            .Select(x => x.Asset)
            .DistinctBy(x => (x.Id, x.Kind))
            .ToList();
        if (assets is [var asset] && asset.Source is AssetSource.External external)
        {
            var verb = items.Any(x => x.Existing is not null) ? "Update" : "Add";
            return AnsiConsole.Confirm(
                $"{verb} [bold]{Markup.Escape(asset.Id)}[/] from [blue]{Markup.Escape(ExternalSourceParser.RepositoryLabel(external))}[/]?",
                defaultValue: true);
        }

        return Confirm("Apply these changes?");
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

    private static string Label(Asset asset, IReadOnlyDictionary<string, AssetInstallMarker>? installed)
    {
        // Fit the description to the terminal: id + version + prompt chrome eat ~30 columns.
        var room = Math.Clamp(AnsiConsole.Profile.Width - asset.Id.Length - 30, 24, 100);
        var description = Output.Fit(asset.Description, room);
        var badge = asset.Status switch
        {
            AssetStatus.Experimental => " [grey](experimental)[/]",
            AssetStatus.Deprecated => " [yellow](deprecated)[/]",
            _ => ""
        };
        var marker = installed is not null && installed.TryGetValue(asset.Id, out var state)
            ? state switch
            {
                AssetInstallMarker.UpdateAvailable => " [blue](update available)[/]",
                _ => " [green](installed)[/]"
            }
            : "";
        return $"{Markup.Escape(asset.Id)} [grey]{asset.Version}[/]  {Markup.Escape(description)}{marker}{badge}";
    }

    private sealed record AssetChoice(string Label, Asset? Asset);

    private sealed record KindChoice(string Label, AssetKind? Kind = null, bool Done = false, bool Cancel = false, bool Search = false);
}
