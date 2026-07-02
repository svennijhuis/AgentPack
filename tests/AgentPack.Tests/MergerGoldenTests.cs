using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// Golden-file tests for provider config merges: exact output per provider format.
/// These are the tests that catch provider format drift.
/// </summary>
public class MergerGoldenTests
{
    private static McpServer GitHubMcp => new()
    {
        Server = "github",
        Command = "github-mcp-server",
        EnvVars = ["GITHUB_TOKEN"]
    };

    [Fact]
    public void ClaudeProjectMcpGolden()
    {
        var output = MergeMcp(ProviderName.Claude, InstallScope.Project, ".mcp.json");
        Assert.Equal(Normalize("""
            {
              "mcpServers": {
                "github": {
                  "type": "stdio",
                  "command": "github-mcp-server",
                  "args": [],
                  "env": {
                    "GITHUB_TOKEN": "${GITHUB_TOKEN}"
                  }
                }
              }
            }
            """), Normalize(output));
    }

    [Fact]
    public void CopilotProjectMcpUsesServersRootKey()
    {
        var output = MergeMcp(ProviderName.Copilot, InstallScope.Project, Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("\"servers\"", output);
        Assert.DoesNotContain("\"mcpServers\"", output);
    }

    [Fact]
    public void CopilotUserMcpUsesMcpServersRootKey()
    {
        var output = MergeMcp(ProviderName.Copilot, InstallScope.User, Path.Combine(".copilot", "mcp-config.json"));
        Assert.Contains("\"mcpServers\"", output);
    }

    [Fact]
    public void CodexMcpTomlGolden()
    {
        var output = MergeMcp(ProviderName.Codex, InstallScope.Project, Path.Combine(".codex", "config.toml"));
        Assert.Equal(Normalize("""
            [mcp_servers.github]
            command = "github-mcp-server"
            env_vars = ["GITHUB_TOKEN"]
            """), Normalize(output));
    }

    [Fact]
    public void McpMergePreservesUnrelatedUserEntries()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, """
            {
              "mcpServers": {
                "mine": { "type": "stdio", "command": "my-server" }
              },
              "otherSetting": true
            }
            """);

        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"mine\"", output);
        Assert.Contains("\"otherSetting\"", output);
        Assert.Contains("\"github\"", output);
    }

    [Fact]
    public void McpMergeRejectsConflictingExistingServer()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, """
            { "mcpServers": { "github": { "type": "stdio", "command": "different-server" } } }
            """);

        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { }));
        Assert.Equal(ExitCodes.DriftOrConflict, ex.ExitCode);
    }

    [Fact]
    public void ClaudeHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Tool = "Bash", Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".claude", "settings.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Hooks, Path.Combine(".claude", "settings.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "./.claude/hooks/secret-scan/hook.sh",
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));

        var installedHook = Path.Combine(temp.Path, ".claude", "hooks", "secret-scan", "hook.sh");
        Assert.True(File.Exists(installedHook));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(installedHook).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void CursorHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".cursor", "hooks.json");
        var target = new InstallTarget(ProviderName.Cursor, AssetKind.Hooks, Path.Combine(".cursor", "hooks.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "hooks": {
                "beforeShellExecution": [
                  {
                    "command": "./.cursor/hooks/secret-scan/hook.sh"
                  }
                ]
              },
              "version": 1
            }
            """), Normalize(File.ReadAllText(targetPath)));
    }

    [Fact]
    public void HookMergeIsIdempotent()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan", hook: new HookSpec { Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".claude", "settings.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Hooks, Path.Combine(".claude", "settings.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });
        var first = File.ReadAllText(targetPath);
        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(first, File.ReadAllText(targetPath));
    }

    [Fact]
    public void McpSecretsInRawContentAreRejected()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Mcp, "leaky",
            files: new Dictionary<string, string>
            {
                ["mcp.json"] = """{"name":"leaky","transport":"stdio","command":"x","env":{"TOKEN":"actual-secret-value"}}"""
            });
        var sourcePath = Path.Combine(temp.Path, "assets", "mcp", "leaky", "content");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, sourcePath, target, Path.Combine(temp.Path, ".mcp.json"), InstallScope.Project, _ => { }));
        Assert.Contains("must be declared by name", ex.Message);
    }

    private static string MergeMcp(ProviderName provider, InstallScope scope, string relativeTarget)
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var targetPath = Path.Combine(temp.Path, relativeTarget);
        var target = new InstallTarget(provider, AssetKind.Mcp, relativeTarget, InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, scope, _ => { });
        return File.ReadAllText(targetPath);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim().TrimStart('﻿');
}
