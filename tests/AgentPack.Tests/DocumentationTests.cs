namespace AgentPack.Tests;

public class DocumentationTests
{
    [Fact]
    public void CliReferenceCoversEveryCanonicalCommandPath()
    {
        var repoRoot = FindRepoRoot();
        var reference = File.ReadAllText(Path.Combine(repoRoot, "docs", "cli-reference.md"));
        string[] commands =
        [
            "agentpack init", "agentpack list", "agentpack find", "agentpack groups", "agentpack new",
            "agentpack import", "agentpack add", "agentpack plan", "agentpack remove", "agentpack upgrade",
            "agentpack outdated", "agentpack status", "agentpack diff", "agentpack pin", "agentpack unpin",
            "agentpack doctor", "agentpack catalog validate", "agentpack catalog lock",
            "agentpack catalog verify-external", "agentpack profile list", "agentpack profile plan",
            "agentpack profile apply", "agentpack source add", "agentpack source list", "agentpack source sync"
        ];

        foreach (var command in commands)
        {
            Assert.Contains(command, reference, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AgentPack.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not find the AgentPack repository root.");
    }
}
