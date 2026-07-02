using System.Diagnostics;

namespace AgentPack.Tests;

/// <summary>
/// End-to-end tests running the real CLI binary against a temp catalog repo.
/// </summary>
public class CliEndToEndTests
{
    [Fact]
    public void HelpSucceeds()
    {
        using var temp = new TempDir();
        var result = RunCli(temp, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("agentpack", result.Output);
        Assert.Contains("add", result.Output);
    }

    [Fact]
    public void UnknownCommandSuggestsNearestAndFails()
    {
        using var temp = new TempDir();
        var result = RunCli(temp, "statsu");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Did you mean 'agentpack status'?", result.Output + result.Error);
    }

    [Fact]
    public void NewScaffoldsManifestAndContent()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var result = RunCli(temp, "new", "skills", "grill-me", "--group", "review");
        Assert.Equal(0, result.ExitCode);

        var manifest = Path.Combine(WorkDir(temp), "assets", "skills", "grill-me", "agentpack.yaml");
        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), "assets", "skills", "grill-me", "content", "SKILL.md")));
        var yaml = File.ReadAllText(manifest);
        Assert.Contains("name: Grill Me", yaml);
        Assert.DoesNotContain("checksum", yaml);

        // A second run without --force refuses to overwrite.
        var again = RunCli(temp, "new", "skills", "grill-me");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("--force", again.Output + again.Error);
    }

    [Fact]
    public void ImportRequiresPinnedRef()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);

        var floating = RunCli(temp, "import", "https://github.com/example/skills/tree/main/skills/pdf");
        Assert.NotEqual(0, floating.ExitCode);
        Assert.Contains("pin", (floating.Output + floating.Error).ToLowerInvariant());

        var pinned = RunCli(temp, "import",
            "https://github.com/example/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d");
        Assert.Equal(0, pinned.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(WorkDir(temp), "assets", "skills", "pdf", "agentpack.yaml"));
        Assert.Contains("source: https://github.com/example/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d", manifest);
    }

    [Fact]
    public void AddInstallsAndStatusReportsIt()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");
        Directory.CreateDirectory(Path.Combine(WorkDir(temp), ".claude"));

        var add = RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(0, add.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill", "SKILL.md")));

        var status = RunCli(temp, "status", "--project");
        Assert.Contains("demo-skill", status.Output);
    }

    [Fact]
    public void AddUnknownAssetSuggestsNearest()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "demo-skil", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("demo-skill", result.Output + result.Error);
    }

    [Fact]
    public void BlockedAssetCannotBeInstalledExplicitly()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "banned", "status: blocked");

        var result = RunCli(temp, "add", "banned", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("blocked", (result.Output + result.Error).ToLowerInvariant());
    }

    [Fact]
    public void AddWithoutTerminalAndWithoutArgsGivesGuidance()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "--claude", "--project");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("agentpack add grill-me", result.Output + result.Error);
    }

    [Fact]
    public void AddEverythingWithYesIsRefused()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("entire catalog", result.Output + result.Error);
    }

    [Fact]
    public void AddByKindInstallsAllOfKindWhenNonInteractive()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "skill-one");
        WriteSkill(temp, "skill-two");

        var result = RunCli(temp, "add", "skills", "--claude", "--project", "--yes");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-one", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-two", "SKILL.md")));
    }

    [Fact]
    public void CatalogLockThenValidatePasses()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        Assert.Equal(0, RunCli(temp, "catalog", "lock", "--no-fetch").ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), "catalog.lock.yaml")));

        var validate = RunCli(temp, "catalog", "validate");
        Assert.Equal(0, validate.ExitCode);

        // Content edit without relock: validation must fail on checksum mismatch.
        File.WriteAllText(Path.Combine(WorkDir(temp), "assets", "skills", "demo-skill", "content", "SKILL.md"), "changed\n");
        var stale = RunCli(temp, "catalog", "validate");
        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("mismatch", (stale.Output + stale.Error).ToLowerInvariant());
    }

    private static string WorkDir(TempDir temp) => Path.Combine(temp.Path, "repo");

    private static void WriteCatalog(TempDir temp)
    {
        Directory.CreateDirectory(WorkDir(temp));
        File.WriteAllText(Path.Combine(WorkDir(temp), "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.1.0
            groups:
              - id: review
                name: Review
            """);
    }

    private static void WriteSkill(TempDir temp, string id, string? extraYaml = null)
    {
        var dir = Path.Combine(WorkDir(temp), "assets", "skills", id);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "SKILL.md"), $"# {id}\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            $"name: {id}\nversion: 1.0.0\ndescription: Test skill.\ngroups: [review]\n{extraYaml}\n");
    }

    private static (int ExitCode, string Output, string Error) RunCli(TempDir temp, params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "AgentPack.Cli.dll");
        Assert.True(File.Exists(cliDll), $"CLI binary not found at {cliDll}");

        Directory.CreateDirectory(WorkDir(temp));
        var home = Path.Combine(temp.Path, "home", ".agentpack");
        Directory.CreateDirectory(home);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkDir(temp),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["AGENTPACK_HOME"] = home;
        start.Environment["CI"] = "1";
        start.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "CLI process timed out");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
