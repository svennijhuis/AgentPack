namespace AgentPack.Core;

/// <summary>
/// Runs a git or gh command and turns a non-zero exit into a product error. Each caller
/// builds one of these with the hint that fits its situation, so the "check the exit
/// code, quote the first stderr line, throw" shape exists once instead of per class.
/// </summary>
public sealed class GitRunner
{
    private readonly string _fileName;
    private readonly string _hint;

    public GitRunner(string hint, string fileName = "git")
    {
        _fileName = fileName;
        _hint = hint;
    }

    /// <summary>
    /// <paramref name="failure"/> completes the sentence "&lt;failure&gt;: &lt;git's first
    /// stderr line&gt;", so it should read as what could not be done.
    /// </summary>
    public ProcessResult Run(IReadOnlyList<string> arguments, string workingDirectory, string failure)
    {
        var result = ProcessRunner.Run(_fileName, arguments, workingDirectory);
        if (result.ExitCode == 0) return result;
        throw new AgentPackException($"{failure}: {ProcessRunner.FirstLine(result.Error)}", _hint);
    }
}
