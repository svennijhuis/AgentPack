using System.Reflection;

namespace AgentPack.Cli.Ui;

public static class VersionInfo
{
    /// <summary>The tool version as packed (e.g. "1.0.0"), without build metadata.</summary>
    public static string Current
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational)) return "0.0.0";
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }
    }
}
