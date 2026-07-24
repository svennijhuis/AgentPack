using AgentPack.Core;
using Spectre.Console;

namespace AgentPack.Cli.Ui;

public static class GettingStarted
{
    public static void Show()
    {
        AnsiConsole.MarkupLine($"[bold]AgentPack {Markup.Escape(VersionInfo.Current)}[/]");
        AnsiConsole.MarkupLine("[grey]Share approved AI skills, hooks, MCP servers, prompts, rules, and agents.[/]");
        AnsiConsole.WriteLine();

        Section("Install from the catalog",
            "agentpack list",
            "agentpack search <query>",
            "agentpack install",
            "agentpack install <id> --user",
            "agentpack install <id> --project");
        AnsiConsole.MarkupLine("[grey]  'agentpack install' with no id opens a picker — tick assets, press Enter to install (or update when newer).[/]");
        AnsiConsole.WriteLine();
        Section("Contribute to the catalog",
            "agentpack submit <kind> <path-or-url-or-id>");
        Section("Configuration and updates",
            "agentpack config",
            "agentpack catalog status",
            "agentpack catalog sync",
            "agentpack update");

        AnsiConsole.MarkupLine("[grey]Run[/] [blue]agentpack --help[/] [grey]for every command or[/] [blue]agentpack help <command>[/] [grey]for details.[/]");
    }

    private static void Section(string title, params string[] commands)
    {
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        foreach (var command in commands)
        {
            AnsiConsole.MarkupLine($"  [blue]{Markup.Escape(command)}[/]");
        }

        AnsiConsole.WriteLine();
    }
}
