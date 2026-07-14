using AgentPack.Core;
using AgentPack.Core.Primitives;

namespace AgentPack.Tests;

public class InstallerTests
{
    [Fact]
    public void InstallsSkillAndWritesRelativeLockPath()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);

        var plan = installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project);
        var item = Assert.Single(plan.Items);
        Assert.Equal(InstallState.Available, item.State);

        var results = installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Keep);
        Assert.Equal(ApplyOutcome.Installed, Assert.Single(results).Outcome);
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo", "SKILL.md")));

        var lockFile = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath);
        var entry = Assert.Single(lockFile.Entries);
        Assert.Equal(".claude/skills/demo", entry.Path);
        Assert.Equal(AssetKind.Skills, entry.Kind);
        Assert.Equal(ProviderName.Claude, entry.Provider);
        Assert.True(Directory.Exists(entry.ManagedSnapshotPath));
    }

    [Fact]
    public void UnsupportedProviderCombinationsAreReportedNotSilent()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Rules, "style",
            files: new Dictionary<string, string> { ["style.mdc"] = "rule\n" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);

        var plan = new Installer(paths).Plan(loaded, [asset], ProviderNames.All, InstallScope.Project);

        Assert.Single(plan.Items); // cursor only
        Assert.Equal(3, plan.Skipped.Count); // claude + codex + copilot, with reasons
        Assert.All(plan.Skipped, skip => Assert.False(string.IsNullOrWhiteSpace(skip.Reason)));
    }

    [Fact]
    public void HooksInstallForAllFourProviders()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard",
            hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);

        var plan = installer.Plan(loaded, [asset], ProviderNames.All, InstallScope.Project);
        Assert.Equal(4, plan.Items.Count);
        Assert.Empty(plan.Skipped);

        var results = installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Keep);
        Assert.All(results, r => Assert.Equal(ApplyOutcome.Installed, r.Outcome));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".codex", "hooks.json")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".github", "hooks", "guard.json")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".cursor", "hooks.json")));
    }

    [Fact]
    public void RemovingCopilotHookDeletesItsConfigFileAndContent()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard",
            hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Copilot, ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        installer.Remove(kind: null, ["guard"], providers: null, InstallScope.Project);

        // Copilot's per-asset file and content are gone; Claude's shared settings.json survives.
        Assert.False(File.Exists(Path.Combine(paths.WorkingDirectory, ".github", "hooks", "guard.json")));
        Assert.False(Directory.Exists(Path.Combine(paths.WorkingDirectory, ".github", "hooks", "guard")));
        Assert.False(Directory.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "hooks", "guard")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "settings.json")));
        Assert.Empty(JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries);
    }

    [Fact]
    public void LocalEditsAreDetectedAndDriftDecisionIsHonored()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        // User edits the installed copy.
        var installedFile = Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo", "SKILL.md");
        File.WriteAllText(installedFile, "my local tweak\n");

        var newer = asset with { Version = SemVersion.Parse("1.1.0") };
        var loadedNewer = TestData.Loaded(paths.WorkingDirectory, newer);
        var plan = installer.Plan(loadedNewer, [newer], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.LocalChanges, Assert.Single(plan.Items).State);

        // Keep: content untouched.
        var kept = installer.Apply(plan.Items, loadedNewer, InstallScope.Project, _ => DriftAction.Keep);
        Assert.Equal(ApplyOutcome.KeptLocalChanges, Assert.Single(kept).Outcome);
        Assert.Equal("my local tweak\n", File.ReadAllText(installedFile));

        // Overwrite: catalog content restored, version bumped in lock.
        var overwritten = installer.Apply(plan.Items, loadedNewer, InstallScope.Project, _ => DriftAction.Overwrite);
        Assert.Equal(ApplyOutcome.Updated, Assert.Single(overwritten).Outcome);
        Assert.NotEqual("my local tweak\n", File.ReadAllText(installedFile));
        Assert.Equal("1.1.0", Assert.Single(JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries).Version);
    }

    [Fact]
    public void OverwriteBacksUpTheOldContentFirst()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo", "SKILL.md"), "precious edits\n");
        var plan = installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project);
        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        var backupsRoot = Path.Combine(paths.WorkingDirectory, ".agentpack", "backups");
        var backedUp = Directory.EnumerateFiles(backupsRoot, "SKILL.md", SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f) == "precious edits\n");
        Assert.True(backedUp, "expected the edited content to be backed up before overwrite");
    }

    [Fact]
    public void PinnedInstallsAreSkippedByUpgrade()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items, loaded, InstallScope.Project, _ => DriftAction.Keep);
        installer.SetPinned("demo", pinned: true, InstallScope.Project);

        var newer = asset with { Version = SemVersion.Parse("2.0.0") };
        var loadedNewer = TestData.Loaded(paths.WorkingDirectory, newer);

        Assert.Empty(installer.Outdated(loadedNewer, InstallScope.Project).Items);

        var plan = installer.Plan(loadedNewer, [newer], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.Pinned, Assert.Single(plan.Items).State);
        var results = installer.Apply(plan.Items, loadedNewer, InstallScope.Project, _ => DriftAction.Overwrite);
        Assert.Equal(ApplyOutcome.SkippedPinned, Assert.Single(results).Outcome);
    }

    [Fact]
    public void RemoveDeletesCopiedContentButKeepsSharedConfigs()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var mcp = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "srv" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, mcp);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [skill, mcp], [ProviderName.Claude], InstallScope.Project).Items, loaded, InstallScope.Project, _ => DriftAction.Keep);

        var removed = installer.Remove(kind: null, ["demo", "github"], providers: null, InstallScope.Project);

        Assert.Equal(2, removed.Count);
        Assert.False(Directory.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".mcp.json")), "shared MCP config must not be deleted");
        Assert.Empty(JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries);
    }

    [Fact]
    public void AddingAnotherMcpServerDoesNotMarkManagedServerAsLocallyModified()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var first = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "first",
            mcp: new McpServer { Server = "first", Command = "first-server" });
        var second = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "second",
            mcp: new McpServer { Server = "second", Command = "second-server" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, first, second);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [first], [ProviderName.Cursor], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);
        installer.Apply(installer.Plan(loaded, [second], [ProviderName.Cursor], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        var firstAgain = Assert.Single(installer.Plan(loaded, [first], [ProviderName.Cursor], InstallScope.Project).Items);
        Assert.Equal(InstallState.Installed, firstAgain.State);
    }

    [Fact]
    public void PlanDoesNotCloneExternalAssets()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var external = TestData.Asset(AssetKind.Skills, "remote",
            source: new AssetSource.External("https://github.com/o/r.git", "9d2f1ae187231d8199c64b5b762e1bdf2244733d", "skills/x", null, null));
        var loaded = TestData.Loaded(paths.WorkingDirectory, external);

        var plan = new Installer(paths).Plan(loaded, [external], [ProviderName.Claude], InstallScope.Project);
        var item = Assert.Single(plan.Items);
        Assert.Null(item.SourcePath);
        Assert.False(Directory.Exists(paths.ExternalCacheRoot));
    }

    [Fact]
    public void UnmanagedExistingTargetIsFlagged()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);

        var targetDir = Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "SKILL.md"), "hand-made\n");

        var plan = new Installer(paths).Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.UnmanagedPresent, Assert.Single(plan.Items).State);
    }

    [Fact]
    public void InstructionsInstallAsSingleFile()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Instructions, "org-instructions",
            files: new Dictionary<string, string> { ["org-instructions.md"] = "# Org rules\n" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);

        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items, loaded, InstallScope.Project, _ => DriftAction.Keep);

        var target = Path.Combine(paths.WorkingDirectory, "CLAUDE.md");
        Assert.True(File.Exists(target));
        Assert.False(Directory.Exists(Path.Combine(paths.WorkingDirectory, "CLAUDE.md", "org-instructions.md")));
        Assert.Equal("# Org rules\n", File.ReadAllText(target));
    }
}
