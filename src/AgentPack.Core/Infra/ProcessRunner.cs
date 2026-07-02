using System.Diagnostics;

namespace AgentPack.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static ProcessResult Run(string fileName, string arguments, string workingDirectory, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(start)
            ?? throw new AgentPackException($"Unable to start '{fileName}'.", $"Check that {fileName} is installed and on PATH.");

        // Both streams are drained concurrently: reading them sequentially deadlocks
        // when the child fills the other pipe's buffer.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)(timeout ?? DefaultTimeout).TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the timeout and the kill.
            }

            throw new AgentPackException(
                $"'{fileName} {arguments}' timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s.",
                "Check network access and retry.");
        }

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
