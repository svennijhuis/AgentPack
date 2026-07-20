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

        Section("Use an existing team catalog",
            "agentpack source add org <git-url>",
            "agentpack list",
            "agentpack add");
        Section("Start a personal or project catalog",
            "agentpack init --overlay",
            "agentpack new skills my-skill --overlay");
        Section("Create assets for a shared catalog",
            "agentpack init",
            "agentpack new skills my-skill",
            "agentpack import <pinned-url>@<commit-sha>");
        Section("Daily commands",
            "agentpack find <query>",
            "agentpack status",
            "agentpack upgrade");

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
