using AgentPack.Core;

namespace AgentPack.Tests;

public class JsonStoreAtomicityTests
{
    [Fact]
    public void SaveReplacesExistingFileAndLeavesNoTempFiles()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "lock.json");

        JsonStore.Save(path, new AgentPackLock { Entries = [new LockEntry { Id = "one" }] });
        JsonStore.Save(path, new AgentPackLock { Entries = [new LockEntry { Id = "two" }] });

        var loaded = JsonStore.Load<AgentPackLock>(path);
        Assert.Equal("two", Assert.Single(loaded.Entries).Id);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void SaveCreatesMissingDirectories()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "nested", "deep", "lock.json");

        JsonStore.Save(path, new AgentPackLock());

        Assert.True(File.Exists(path));
    }
}

public class ScopeLockTests
{
    [Fact]
    public void SecondAcquireFailsWhileFirstIsHeld()
    {
        using var temp = new TempDir();

        using var held = ScopeLock.Acquire(temp.Path);
        var ex = Assert.Throws<AgentPackException>(() => ScopeLock.Acquire(temp.Path, TimeSpan.Zero));
        Assert.Equal(ExitCodes.DriftOrConflict, ex.ExitCode);
    }

    [Fact]
    public void AcquireSucceedsAfterRelease()
    {
        using var temp = new TempDir();

        var first = ScopeLock.Acquire(temp.Path);
        first.Dispose();

        using var second = ScopeLock.Acquire(temp.Path, TimeSpan.Zero);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task AcquireWaitsForTheHolderInsteadOfFailingImmediately()
    {
        using var temp = new TempDir();

        var held = ScopeLock.Acquire(temp.Path);
        var release = Task.Run(() =>
        {
            Thread.Sleep(300);
            held.Dispose();
        });

        using var second = ScopeLock.Acquire(temp.Path, TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        await release;
    }
}

public class PartialApplyTests
{
    [Fact]
    public void FailureMidApplyRecordsAlreadyAppliedItemsInLockfile()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var good = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "good");
        var broken = TestData.Asset(AssetKind.Skills, "broken",
            source: new AssetSource.Local("assets/skills/broken/content", null)); // content folder never written
        var loaded = TestData.Loaded(paths.WorkingDirectory, good, broken);
        var installer = new Installer(paths);

        var plan = installer.Plan(loaded, [good, broken], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(2, plan.Items.Count);

        var ex = Assert.Throws<AgentPackException>(() =>
            installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Keep));
        Assert.Contains("broken", ex.Message);

        // The successfully applied item is on disk AND in the lockfile.
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "good", "SKILL.md")));
        var lockFile = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath);
        Assert.Equal("good", Assert.Single(lockFile.Entries).Id);
    }

    [Fact]
    public void RerunAfterFailureSkipsCompletedItems()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var good = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "good");
        var broken = TestData.Asset(AssetKind.Skills, "broken",
            source: new AssetSource.Local("assets/skills/broken/content", null));
        var loaded = TestData.Loaded(paths.WorkingDirectory, good, broken);
        var installer = new Installer(paths);

        Assert.Throws<AgentPackException>(() =>
            installer.Apply(installer.Plan(loaded, [good, broken], [ProviderName.Claude], InstallScope.Project).Items,
                loaded, InstallScope.Project, _ => DriftAction.Keep));

        // Fix the broken asset's content, rerun: 'good' is up to date, 'broken' installs.
        var fixedBroken = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "broken");
        var loadedFixed = TestData.Loaded(paths.WorkingDirectory, good, fixedBroken);
        var results = installer.Apply(
            installer.Plan(loadedFixed, [good, fixedBroken], [ProviderName.Claude], InstallScope.Project).Items,
            loadedFixed, InstallScope.Project, _ => DriftAction.Keep);

        Assert.Equal(ApplyOutcome.AlreadyUpToDate, results.Single(r => r.Item.Asset.Id == "good").Outcome);
        Assert.Equal(ApplyOutcome.Installed, results.Single(r => r.Item.Asset.Id == "broken").Outcome);
        Assert.Equal(2, JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries.Count);
    }
}
