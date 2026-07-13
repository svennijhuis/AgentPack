using System.Diagnostics;

namespace AgentPack.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan? timeout = null)
    {
        // ArgumentList (not a concatenated string) so values can never be
        // re-tokenized by quoting rules into extra arguments.
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

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
                $"'{fileName} {string.Join(' ', arguments)}' timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s.",
                "Check network access and retry.");
        }

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Guards values that end up as git positional arguments (URLs, branches, refs).
    /// A value starting with '-' would be parsed by git as an option — e.g. a branch
    /// named '--upload-pack=...' reaches command execution — so it is rejected outright.
    /// </summary>
    public static string SafeGitArg(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
        {
            throw new AgentPackException(
                $"Invalid {description} '{value}'.",
                $"A {description} cannot be empty or start with '-'.");
        }

        return value;
    }
}
