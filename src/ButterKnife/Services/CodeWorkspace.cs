namespace ButterKnife.Services;

/// <summary>A tool was called with arguments it cannot act on; the message goes back to the model as the tool's error.</summary>
public sealed class CodeToolException(string message) : Exception(message);

/// <summary>
/// The project folder a Code session works in. File tools resolve every path against it and refuse anything that
/// lands outside (".." or an absolute path elsewhere); commands run with it as the working directory. Symbolic links
/// inside the folder are followed as the file system resolves them.
/// </summary>
public sealed class CodeWorkspace
{
    /// <summary>Folders the search tools skip: version control, dependencies and build output are noise to a model.</summary>
    public static readonly IReadOnlySet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv", ".mypy_cache",
        ".pytest_cache", ".gradle", "build", "dist", "target", ".next", ".nuxt", "DerivedData", "Pods", ".terraform",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public CodeWorkspace(string root)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"The folder {full} does not exist.");
        }
        Root = Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>Absolute path of the folder, without a trailing separator (except for a drive or file system root).</summary>
    public string Root { get; }

    /// <summary>
    /// The absolute path for <paramref name="path"/> (relative to the root, or absolute inside it). Empty or "." is the
    /// root. Throws <see cref="CodeToolException"/> for a path outside the folder.
    /// </summary>
    public string Resolve(string? path)
    {
        var trimmed = (path ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == ".")
        {
            return Root;
        }

        if (trimmed.StartsWith('~'))
        {
            throw new CodeToolException($"{trimmed} is outside the project folder; use a path relative to {Root}.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(Root, trimmed)));
        if (!Contains(full))
        {
            throw new CodeToolException($"{trimmed} is outside the project folder ({Root}). Only files inside it can be used.");
        }
        return full;
    }

    public bool Contains(string fullPath) =>
        string.Equals(fullPath, Root, PathComparison)
        || fullPath.StartsWith(Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>The path as the model and the transcript see it: relative to the root, with forward slashes.</summary>
    public string Relative(string fullPath)
    {
        var relative = Path.GetRelativePath(Root, fullPath);
        return relative == "." ? "." : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Every file under <paramref name="directory"/>, depth first, skipping <see cref="IgnoredDirectories"/> and hidden folders.</summary>
    public IEnumerable<string> EnumerateFiles(string directory, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            string[] files;
            string[] folders;
            try
            {
                files = Directory.GetFiles(current);
                folders = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                yield return file;
            }

            Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
            for (var i = folders.Length - 1; i >= 0; i--)
            {
                var name = Path.GetFileName(folders[i]);
                if (!IgnoredDirectories.Contains(name) && !name.StartsWith('.'))
                {
                    pending.Push(folders[i]);
                }
            }
        }
    }

    /// <summary>
    /// Sub-folders of <paramref name="path"/> for the folder picker (hidden ones left out), sorted by name. Works
    /// anywhere on this computer, not only inside a workspace: it is how a workspace is chosen.
    /// </summary>
    public static IReadOnlyList<string> ListFolders(string path)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Where(d => (File.GetAttributes(d) & FileAttributes.Hidden) == 0)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>True when the folder is a git checkout (the system prompt mentions it so the model can use git).</summary>
    public bool IsGitRepository => Directory.Exists(Path.Combine(Root, ".git")) || File.Exists(Path.Combine(Root, ".git"));
}
