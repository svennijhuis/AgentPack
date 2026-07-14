using System.Security.Cryptography;
using System.Text;

namespace AgentPack.Core;

public static class ContentHash
{
    public static string Compute(string path)
    {
        if (File.Exists(path))
        {
            return "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        using var sha = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(path, file).Replace(Path.DirectorySeparatorChar, '/');
            var header = Encoding.UTF8.GetBytes(relative + "\n");
            sha.TransformBlock(header, 0, header.Length, null, 0);
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            var separator = Encoding.UTF8.GetBytes("\n");
            sha.TransformBlock(separator, 0, separator.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return "sha256:" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static void CopyTree(
        string source,
        string destination,
        IReadOnlyCollection<string>? excludedNames = null)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFilePreservingMode(source, destination);
            return;
        }

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var excluded = excludedNames is null
            ? null
            : new HashSet<string>(excludedNames, StringComparer.OrdinalIgnoreCase);
        CopyDirectory(source, destination, excluded);
    }

    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    public static string OfText(string text) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static string ShortKey(params string?[] parts)
    {
        var input = string.Join("", parts.Select(p => p ?? ""));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes[..12]).ToLowerInvariant();
    }

    public static string ComputeText(string text) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void CopyFilePreservingMode(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
    }

    private static void CopyDirectory(string source, string destination, HashSet<string>? excluded)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (excluded?.Contains(Path.GetFileName(directory)) == true) continue;
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), excluded);
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (excluded?.Contains(Path.GetFileName(file)) == true) continue;
            CopyFilePreservingMode(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
