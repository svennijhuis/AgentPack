using AgentPack.Cli.Commands;
using AgentPack.Cli.Ui;
using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// Guards the paths a user hits first: picking providers for the scope they asked for,
/// and finding the new name of a command that was renamed.
/// </summary>
public class ProviderResolutionTests
{
    [Fact]
    public void UserScopeDetectsProvidersFromTheHomeDirectoryItInstallsInto()
    {
        using var temp = new TempDir();
        var providerHome = Path.Combine(temp.Path, "home");
        var workingDirectory = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(Path.Combine(providerHome, ".claude"));
        Directory.CreateDirectory(workingDirectory);
        var paths = new AgentPackPaths(Path.Combine(temp.Path, "state"), workingDirectory, providerHome);

        var providers = new ProviderScopeSettings().ResolveProviders(paths, InstallScope.User);

        Assert.Equal([ProviderName.Claude], providers);
    }

    [Fact]
    public void ProjectScopePrefersTheRepositoryOverTheHomeDirectory()
    {
        using var temp = new TempDir();
        var providerHome = Path.Combine(temp.Path, "home");
        var workingDirectory = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(providerHome, ".claude"));
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".cursor"));
        var paths = new AgentPackPaths(Path.Combine(temp.Path, "state"), workingDirectory, providerHome);

        var providers = new ProviderScopeSettings().ResolveProviders(paths, InstallScope.Project);

        Assert.Equal([ProviderName.Cursor], providers);
    }

    [Fact]
    public void NoProviderAnywhereNamesTheDirectoryItLookedIn()
    {
        using var temp = new TempDir();
        var providerHome = Path.Combine(temp.Path, "home");
        var workingDirectory = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(providerHome);
        Directory.CreateDirectory(workingDirectory);
        var paths = new AgentPackPaths(Path.Combine(temp.Path, "state"), workingDirectory, providerHome);

        var ex = Assert.Throws<AgentPackException>(() =>
            new ProviderScopeSettings().ResolveProviders(paths, InstallScope.User));

        Assert.Contains(providerHome, ex.Message);
    }
}

public class RenamedCommandTests
{
    [Theory]
    [InlineData("add", "agentpack install")]
    [InlineData("ls", "agentpack list")]
    [InlineData("find", "agentpack search")]
    [InlineData("uninstall", "agentpack remove")]
    [InlineData("upgrade", "agentpack update")]
    [InlineData("plan", "agentpack install --dry-run")]
    [InlineData("new", "agentpack submit")]
    [InlineData("init", "agentpack submit")]
    [InlineData("import", "agentpack submit")]
    [InlineData("source", "agentpack catalog use")]
    public void RenamedCommandsPointAtTheirReplacement(string removed, string replacement)
    {
        var hint = Suggestions.ForParseError($"Unknown command '{removed}'.");

        Assert.NotNull(hint);
        Assert.Contains(replacement, hint);
    }

    [Fact]
    public void ActualTyposStillGetANearestMatch()
    {
        Assert.Contains("agentpack status", Suggestions.ForParseError("Unknown command 'statsu'."));
    }
}
