namespace AgentPack.Core;

/// <summary>
/// Builds the exact, bounded file set copied by <c>agentpack submit</c>. It does
/// not follow symlinks and refuses secret-like files instead of silently publishing
/// them in a pull request. It lives in Core because content resolved by the catalog
/// is scanned by the same rules before it reaches a proposal.
/// </summary>
public static class LocalSubmissionScanner
{
    private const int MaximumFiles = 250;
    private const long MaximumBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".agentpack", ".idea", ".vs", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "coverage", ".next",
        "__pycache__", ".pytest_cache"
    };

    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db"
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".npmrc", ".pypirc", ".netrc", "credentials.json", "secrets.json",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".kdbx"
    };

    public static LocalSubmission Scan(string source, AssetKind kind)
    {
        try
        {
            SafeTree.Attributes(source, $"The submission path '{source}'");
            var isFile = File.Exists(source);
            var root = isFile ? Path.GetDirectoryName(source)! : source;
            var files = new List<SubmissionFile>();
            var ignored = new List<string>();
            var scanned = 0L;

            if (isFile)
            {
                AddFile(source, Path.GetFileName(source), files, ref scanned);
            }
            else
            {
                Walk(source, source, files, ignored, ref scanned);
            }

            if (files.Count == 0)
            {
                throw new AgentPackException("The selected folder has no files that can be submitted.");
            }

            if (kind == AssetKind.Skills && !files.Any(x =>
                    x.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                throw new AgentPackException(
                    "A local skill folder must contain SKILL.md at its top level.",
                    "Select the skill folder itself, not a parent repository or home directory.");
            }

            if (kind is AssetKind.Instructions or AssetKind.Rules or AssetKind.Prompts or AssetKind.Agents && files.Count != 1)
            {
                throw new AgentPackException(
                    $"A {kind.Display()} asset installs as one provider-native file, but this folder contains {files.Count} files.",
                    "Submit the single instruction, rule, prompt, or agent file. Use a skill when supporting files are part of the package.");
            }

            return new LocalSubmission(root, files.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToList(),
                ignored.OrderBy(x => x, StringComparer.Ordinal).ToList(), scanned);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AgentPackException(
                $"AgentPack cannot read part of the selected submission: {ex.Message}",
                "Select a dedicated asset folder containing only files you can review and read.");
        }
    }

    private static void Walk(
        string root,
        string directory,
        List<SubmissionFile> files,
        List<string> ignored,
        ref long scanned)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
            var name = Path.GetFileName(entry);
            var attributes = SafeTree.Attributes(entry, $"The submitted path '{relative}'");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (IgnoredDirectories.Contains(name))
                {
                    ignored.Add(relative + "/");
                    continue;
                }

                Walk(root, entry, files, ignored, ref scanned);
                continue;
            }

            if (IgnoredFiles.Contains(name) || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".user", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase))
            {
                ignored.Add(relative);
                continue;
            }

            AddFile(entry, relative, files, ref scanned);
        }
    }

    // Callers have already rejected symlinks: Scan checks the root it was handed, and
    // Walk checks every entry before descending. Re-stating the file here would only
    // repeat that syscall per file.
    private static void AddFile(string path, string relative, List<SubmissionFile> files, ref long scanned)
    {
        var name = Path.GetFileName(path);
        var isSafeEnvExample = name.Equals(".env.example", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals(".env.sample", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals(".env.template", StringComparison.OrdinalIgnoreCase);
        if ((!isSafeEnvExample && (SensitiveNames.Contains(name) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))) ||
            SensitiveExtensions.Contains(Path.GetExtension(name)))
        {
            throw new AgentPackException(
                $"The local submission contains a secret-like file: {relative}",
                "Remove credentials and private keys. Use environment variable names or example files without real values.");
        }

        var size = new FileInfo(path).Length;
        files.Add(new SubmissionFile(path, relative, size));
        scanned += size;

        // Checked per file rather than once at the end so an enormous tree aborts early
        // instead of being walked in full first.
        if (files.Count > MaximumFiles || scanned > MaximumBytes)
        {
            throw new AgentPackException(
                $"The local submission is too large ({files.Count} files, {ByteSize.Format(scanned)}).",
                $"Keep it below {MaximumFiles} files and {ByteSize.Format(MaximumBytes)}, or submit a smaller asset folder.");
        }
    }

}

public sealed record SubmissionFile(string FullPath, string RelativePath, long Size);

public sealed record LocalSubmission(
    string Root,
    IReadOnlyList<SubmissionFile> Files,
    IReadOnlyList<string> Ignored,
    long TotalBytes);
