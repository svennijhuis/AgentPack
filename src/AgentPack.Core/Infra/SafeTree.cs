namespace AgentPack.Core;

/// <summary>
/// Reviewed content is only ever what a reviewer could actually see. A symlink can
/// point anywhere outside the tree that was reviewed, so it is refused rather than
/// followed — by the submission scanner and by external resolution alike.
/// </summary>
public static class SafeTree
{
    /// <summary>
    /// Attributes of <paramref name="path"/>, refusing symlinks. Returning the
    /// attributes keeps the check to the one stat the caller already needs.
    /// </summary>
    public static FileAttributes Attributes(string path, string description)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new AgentPackException(
                $"{description} is a symlink, which is not allowed.",
                "Replace it with real files so only visible, reviewed content is published.");
        }

        return attributes;
    }
}
