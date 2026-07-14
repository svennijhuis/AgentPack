using AgentPack.Cli.Commands;
using AgentPack.Core;

namespace AgentPack.Tests;

public class CliBehaviorTests
{
    [Theory]
    [InlineData(InstallState.Installed, ExitCodes.Ok)]
    [InlineData(InstallState.LocalChanges, ExitCodes.DriftOrConflict)]
    [InlineData(InstallState.Missing, ExitCodes.DriftOrConflict)]
    public void DiffExitCodeCanGateAutomation(InstallState state, int expected) =>
        Assert.Equal(expected, DiffCommand.ExitCodeFor([state]));

    [Fact]
    public void DiffFailsIfAnyProviderTargetHasDrift() =>
        Assert.Equal(ExitCodes.DriftOrConflict,
            DiffCommand.ExitCodeFor([InstallState.Installed, InstallState.LocalChanges]));
}
