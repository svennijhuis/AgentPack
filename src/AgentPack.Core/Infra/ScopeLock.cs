namespace AgentPack.Core;

/// <summary>
/// Cross-process mutex for a lockfile's directory, held for the duration of any
/// operation that mutates installed content or the lockfile. Backed by an
/// exclusively-opened <c>.lock</c> file; the OS releases the handle even if the
/// process is killed, so a leftover file is never stale.
/// </summary>
public sealed class ScopeLock : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly FileStream _stream;

    private ScopeLock(FileStream stream) => _stream = stream;

    public static ScopeLock Acquire(string lockDirectory, TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(lockDirectory);
        var path = Path.Combine(lockDirectory, ".lock");
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (true)
        {
            try
            {
                return new ScopeLock(new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                throw new AgentPackException(
                    $"Another agentpack process is working in this scope (lock held at {path}).",
                    "Wait for it to finish and retry.",
                    ExitCodes.DriftOrConflict);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
