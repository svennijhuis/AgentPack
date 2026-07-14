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

    public static IEnumerable<object?[]> HookTriggerMatrix()
    {
        foreach (var provider in ProviderNames.All)
            foreach (var (trigger, events) in new (HookTrigger Trigger, string?[] Events)[]
                     {
                     (HookTrigger.PreToolUse, new[] { "PreToolUse", "PreToolUse", "preToolUse", "preToolUse" }),
                     (HookTrigger.PostToolUse, new[] { "PostToolUse", "PostToolUse", "postToolUse", "postToolUse" }),
                     (HookTrigger.Stop, new[] { "Stop", "Stop", "agentStop", "stop" }),
                     (HookTrigger.SessionStart, new[] { "SessionStart", "SessionStart", "sessionStart", "sessionStart" }),
                     (HookTrigger.UserPromptSubmit, new[] { "UserPromptSubmit", "UserPromptSubmit", "userPromptSubmitted", "beforeSubmitPrompt" }),
                     (HookTrigger.Notification, new string?[] { "Notification", null, "notification", null })
                     })
            {
                yield return [provider, trigger, events[(int)provider]];
            }
    }

    [Theory]
    [MemberData(nameof(HookTriggerMatrix))]
    public void EveryHookTriggerUsesTheProviderNativeEventOrFailsExplicitly(
        ProviderName provider, HookTrigger trigger, string? expectedEvent)
    {
        var asset = TestData.Asset(AssetKind.Hooks, "event-test",
            hook: new HookSpec { Trigger = trigger, Command = "hook.sh" });
        var supported = Assert.IsType<ProviderPlan.Supported>(ProviderRegistry.Get(provider).Plan(asset, false));

        if (expectedEvent is null)
        {
            var ex = Assert.Throws<AgentPackException>(() => HookMerger.Preview(asset, supported.Target, "unused"));
            Assert.Contains("do not support", ex.Message);
            return;
        }

        Assert.Contains($"\"{expectedEvent}\"", HookMerger.Preview(asset, supported.Target, "unused"));
    }

    [Fact]
    public void EveryProviderScopeAndMcpTransportRendersNativeSecretReferences()
    {
        foreach (var provider in ProviderNames.All)
            foreach (var scope in Enum.GetValues<InstallScope>())
                foreach (var transport in Enum.GetValues<McpTransport>())
                {
                    var mcp = transport == McpTransport.Stdio
                        ? new McpServer { Server = "matrix", Command = "matrix-server", EnvVars = ["TOKEN"] }
                        : new McpServer
                        {
                            Server = "matrix",
                            Transport = transport,
                            Url = "https://example.test/mcp",
                            HeaderEnvVars = new Dictionary<string, string> { ["Authorization"] = "TOKEN" }
                        };
                    var asset = TestData.Asset(AssetKind.Mcp, "matrix", mcp: mcp);
                    var supported = Assert.IsType<ProviderPlan.Supported>(
                        ProviderRegistry.Get(provider).Plan(asset, scope == InstallScope.User));

                    var output = McpMerger.Preview(asset, null, supported.Target, scope);
                    Assert.Contains("matrix", output);
                    Assert.DoesNotContain("secret-value", output, StringComparison.OrdinalIgnoreCase);
                    if (provider == ProviderName.Codex)
                    {
                        Assert.Contains(transport == McpTransport.Stdio ? "env_vars = [\"TOKEN\"]" : "env_http_headers", output);
                        Assert.DoesNotContain("${", output);
                    }
                    else
                    {
                        Assert.Contains(provider == ProviderName.Cursor ? "${env:TOKEN}" : "${TOKEN}", output);
                    }
                }
    }

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
    public void CopilotProjectMcpGolden()
    {
        // Copilot workspace config: native mcpServers root and shell env expansion.
        var output = MergeMcp(ProviderName.Copilot, InstallScope.Project, Path.Combine(".github", "mcp.json"));
        Assert.Contains("\"mcpServers\"", output);
        Assert.Contains("\"GITHUB_TOKEN\": \"${GITHUB_TOKEN}\"", output);
        Assert.Contains("\"tools\": [", output);
        Assert.Contains("\"*\"", output);
    }

    [Fact]
    public void CopilotUserMcpGolden()
    {
        // Copilot CLI expands ${VAR} references in MCP environment values.
        var output = MergeMcp(ProviderName.Copilot, InstallScope.User, Path.Combine(".copilot", "mcp-config.json"));
        Assert.Equal(Normalize("""
            {
              "mcpServers": {
                "github": {
                  "type": "stdio",
                  "command": "github-mcp-server",
                  "args": [],
                  "env": {
                    "GITHUB_TOKEN": "${GITHUB_TOKEN}"
                  },
                  "tools": [
                    "*"
                  ]
                }
              }
            }
            """), Normalize(output));
    }

    [Fact]
    public void CursorMcpUsesEnvColonPlaceholders()
    {
        var output = MergeMcp(ProviderName.Cursor, InstallScope.Project, Path.Combine(".cursor", "mcp.json"));
        Assert.Contains("\"mcpServers\"", output);
        Assert.Contains("\"GITHUB_TOKEN\": \"${env:GITHUB_TOKEN}\"", output);
    }

    [Fact]
    public void CodexHttpMcpUsesEnvHttpHeaders()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "remote-api", mcp: new McpServer
        {
            Server = "remote-api",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/mcp",
            HeaderEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "API_TOKEN" }
        });
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("env_http_headers = { Authorization = \"API_TOKEN\" }", output);
        Assert.DoesNotContain("${", output);
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
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".cursor", "hooks.json");
        var target = new InstallTarget(ProviderName.Cursor, AssetKind.Hooks, Path.Combine(".cursor", "hooks.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "./.cursor/hooks/secret-scan/hook.sh",
                    "timeout": 30
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));
    }

    [Fact]
    public void CodexHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Tool = "Bash", Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".codex", "hooks.json");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Hooks, Path.Combine(".codex", "hooks.json"), InstallMode.MergeHook);

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
                        "command": "./.codex/hooks/secret-scan/hook.sh",
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));
    }

    [Fact]
    public void CopilotHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "secret-scan.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks, Path.Combine(".github", "hooks", "secret-scan.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "type": "command",
                    "bash": "./.github/hooks/secret-scan/.agentpack-copilot-pre-tool.sh",
                    "timeoutSec": 30
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));

        Assert.True(File.Exists(Path.Combine(temp.Path, ".github", "hooks", "secret-scan", "hook.sh")));
        Assert.True(File.Exists(Path.Combine(temp.Path, ".github", "hooks", "secret-scan", ".agentpack-copilot-pre-tool.sh")));
    }

    [Fact]
    public void CopilotHookIncludesPowershellTwinWhenPresent()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "cross-os",
            files: new Dictionary<string, string>
            {
                ["hook.sh"] = "#!/usr/bin/env bash\necho ok\n",
                ["hook.ps1"] = "Write-Output 'ok'\n"
            },
            hook: new HookSpec { Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "cross-os", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "cross-os.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks, Path.Combine(".github", "hooks", "cross-os.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"bash\": \"./.github/hooks/cross-os/.agentpack-copilot-pre-tool.sh\"", output);
        Assert.Contains("\"powershell\": \"./.github/hooks/cross-os/.agentpack-copilot-pre-tool.ps1\"", output);
    }

    [Fact]
    public void CopilotPreToolWrapperTranslatesPortableExitTwoIntoStructuredDenial()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "deny-test",
            files: new Dictionary<string, string>
            {
                ["hook.sh"] = "#!/usr/bin/env bash\ncat >/dev/null\necho '{\"permission\":\"deny\"}'\nexit 2\n"
            },
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "deny-test", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "deny-test.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks,
            Path.Combine(".github", "hooks", "deny-test.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        var wrapper = Path.Combine(temp.Path, ".github", "hooks", "deny-test", ".agentpack-copilot-pre-tool.sh");
        var start = new System.Diagnostics.ProcessStartInfo("bash", wrapper)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        process.StandardInput.Write("{\"toolName\":\"shell\"}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrEmpty(stderr), stderr);
        Assert.Contains("\"permissionDecision\":\"deny\"", stdout);
        Assert.DoesNotContain("\"permission\":\"deny\"", stdout);
    }

    [Fact]
    public void HookCommandCannotEscapeInstalledSupportDirectory()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "escape-test",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "../outside.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "escape-test", "content");
        var targetPath = Path.Combine(temp.Path, ".claude", "settings.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Hooks,
            Path.Combine(".claude", "settings.json"), InstallMode.MergeHook);

        var ex = Assert.Throws<AgentPackException>(() =>
            HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { }));

        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
        Assert.Contains("escapes its allowed root", ex.Message);
        Assert.False(File.Exists(targetPath));
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

    [Fact]
    public void RawMcpServersAreNormalizedForEachProvider()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "mcp.json");
        File.WriteAllText(source, """
            {
              "mcpServers": {
                "demo": {
                  "type": "stdio",
                  "command": "demo-server",
                  "env": { "TOKEN": "${TOKEN}" }
                }
              }
            }
            """);
        var asset = TestData.Asset(AssetKind.Mcp, "demo");

        var cursor = McpMerger.BuildServers(asset, source, ProviderName.Cursor, InstallScope.Project);
        Assert.Equal("${env:TOKEN}", cursor["demo"]!["env"]!["TOKEN"]!.GetValue<string>());

        var copilotUser = McpMerger.BuildServers(asset, source, ProviderName.Copilot, InstallScope.User);
        Assert.Equal("${TOKEN}", copilotUser["demo"]!["env"]!["TOKEN"]!.GetValue<string>());
        Assert.Equal("*", copilotUser["demo"]!["tools"]![0]!.GetValue<string>());

        var codexTarget = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, ".codex/config.toml", InstallMode.MergeMcp);
        Assert.Contains("env_vars = [\"TOKEN\"]", McpMerger.Preview(asset, source, codexTarget, InstallScope.Project));
    }

    [Fact]
    public void RawMcpServersCannotBypassSecretRejection()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "mcp.json");
        File.WriteAllText(source,
            "{\"mcpServers\":{\"demo\":{\"command\":\"run\",\"env\":{\"TOKEN\":\"literal-secret\"}}}}");

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.BuildServers(TestData.Asset(AssetKind.Mcp, "demo"), source, ProviderName.Claude, InstallScope.Project));

        Assert.Contains("declared by name", ex.Message);
    }

    [Fact]
    public void InvalidExistingMcpJsonIsReportedAsValidationError()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, "{ not-json");
        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { }));

        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
        Assert.Contains("valid JSON object", ex.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonObjectExistingMcpJsonIsNotOverwritten(string json)
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, json);
        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { }));

        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
        Assert.Equal(json, File.ReadAllText(targetPath));
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
