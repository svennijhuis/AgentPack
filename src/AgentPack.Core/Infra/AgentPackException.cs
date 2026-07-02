namespace AgentPack.Core;

/// <summary>Standard exit codes for the CLI.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int UserError = 1;
    public const int ValidationFailed = 2;
    public const int DriftOrConflict = 3;
    public const int Internal = 70;
}

/// <summary>
/// An expected, user-facing error: rendered as a message plus an optional
/// "try this next" hint, without a stack trace.
/// </summary>
public sealed class AgentPackException(string message, string? hint = null, int exitCode = ExitCodes.UserError)
    : Exception(message)
{
    public string? Hint { get; } = hint;
    public int ExitCode { get; } = exitCode;
}
