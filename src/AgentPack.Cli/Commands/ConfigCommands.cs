using System.ComponentModel;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

/// <summary>
/// Shows every path agentpack uses and lets the user relocate its state directory
/// (the "home"). Provider files (.claude/, .codex/, ...) are never relocated here —
/// those always live in the OS user profile.
/// </summary>
public sealed class ConfigCommand : Command<ConfigCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--set-home <PATH>")]
        [Description("Persist the directory where agentpack keeps its state (catalog cache, locks, config).")]
        public string? SetHome { get; set; }

        [CommandOption("--reset-home")]
        [Description("Clear a persisted home and revert to the default (~/.agentpack).")]
        public bool ResetHome { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings.SetHome is not null && settings.ResetHome)
        {
            throw new AgentPackException("--set-home and --reset-home cannot be combined.");
        }

        if (settings.SetHome is not null)
        {
            if (string.IsNullOrWhiteSpace(settings.SetHome))
            {
                throw new AgentPackException("--set-home needs a directory path.", "Example: agentpack config --set-home ~/dotfiles/agentpack");
            }

            var target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.SetHome));
            AgentPackPaths.PersistHome(target);
            Output.Success($"agentpack home set to {target}.");
            Output.Info("Existing state is not moved automatically — copy the old home's contents over to keep your installs and catalog cache.");
            if (AgentPackPaths.HomeSetByEnvironment)
            {
                Output.Warning("AGENTPACK_HOME is set in your environment and overrides this until you unset it.");
            }
        }
        else if (settings.ResetHome)
        {
            AgentPackPaths.ClearPersistedHome();
            Output.Success($"agentpack home reset to the default ({AgentPackPaths.DefaultHome}).");
        }

        ShowPaths();
        return ExitCodes.Ok;
    }

    private static void ShowPaths()
    {
        var paths = new CliSession().Paths;
        var homeSource = AgentPackPaths.HomeSetByEnvironment
            ? "AGENTPACK_HOME (environment)"
            : AgentPackPaths.PersistedHome() is not null
                ? "config (agentpack config --set-home)"
                : "default";

        Output.Table(
            ["Setting", "Value", "Where it comes from"],
            new[]
            {
                new[] { "home", paths.Home, homeSource },
                ["config file", paths.ConfigPath, ""],
                ["catalog cache", paths.CacheRoot, ""],
                ["user lockfile", paths.UserLockPath, ""],
                ["provider home", paths.ProviderHome, "your OS user profile — where --user writes .claude/, .codex/, ..."],
            });

        Output.Info("'home' holds agentpack's own state. User-scope installs (--user) always land in the provider home above.");
        Output.Info("Change the home with 'agentpack config --set-home <path>' (or set AGENTPACK_HOME for one-off overrides).");
    }
}
