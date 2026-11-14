namespace DiffWeave.Core.Directories;

/// <summary>What exists at a path on one side of a directory comparison.</summary>
public enum EntryKind
{
    /// <summary>Nothing.</summary>
    Missing,

    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link (only with <see cref="SymlinkHandling.CompareTargets"/>).</summary>
    Symlink,

    /// <summary>Something else (device, socket, unreadable entry).</summary>
    Other,
}

/// <summary>The comparison status of an entry.</summary>
public enum EntryStatus
{
    /// <summary>Present everywhere and equal (directories: all children identical).</summary>
    Identical,

    /// <summary>Present everywhere with the same kind, but different (directories: some child is not identical).</summary>
    Different,

    /// <summary>Present only in the first directory (A).</summary>
    OnlyInA,

    /// <summary>Present only in the second directory (B).</summary>
    OnlyInB,

    /// <summary>Present only in the third directory (C).</summary>
    OnlyInC,

    /// <summary>Three-way: missing in A, present in B and C.</summary>
    MissingInA,

    /// <summary>Three-way: missing in B, present in A and C.</summary>
    MissingInB,

    /// <summary>Three-way: missing in C, present in A and B.</summary>
    MissingInC,

    /// <summary>Present on several sides with different kinds (e.g. file vs directory).</summary>
    TypeMismatch,

    /// <summary>Could not be compared (I/O error, see <see cref="DirectoryComparisonNode.Error"/>).</summary>
    Error,
}

/// <summary>Which pairs of sides are equal (for files present on several sides).</summary>
[Flags]
public enum SidesEqual
{
    /// <summary>No pair is known to be equal.</summary>
    None = 0,

    /// <summary>A equals B.</summary>
    AB = 1,

    /// <summary>A equals C.</summary>
    AC = 2,

    /// <summary>B equals C.</summary>
    BC = 4,
}

/// <summary>How files are judged equal.</summary>
[Flags]
public enum DirectoryCompareMode
{
    /// <summary>Equal size and last-write time (within <see cref="DirectoryCompareOptions.TimeTolerance"/>) means equal. Fast.</summary>
    SizeAndTime = 1,

    /// <summary>Compare contents (size first, then bytes / hash). Exact.</summary>
    Content = 2,

    /// <summary>Equal size and time is trusted; otherwise contents are compared.</summary>
    SizeAndTimeThenContent = SizeAndTime | Content,
}

/// <summary>How symbolic links are handled.</summary>
public enum SymlinkHandling
{
    /// <summary>Links are entries of kind <see cref="EntryKind.Symlink"/> compared by their target text (not followed).</summary>
    CompareTargets,

    /// <summary>Links are followed (cycles are detected and reported as errors).</summary>
    Follow,

    /// <summary>Links are ignored.</summary>
    Skip,
}

/// <summary>Options of <see cref="DirectoryComparer"/>.</summary>
public sealed record DirectoryCompareOptions
{
    /// <summary>Default options.</summary>
    public static DirectoryCompareOptions Default { get; } = new();

    /// <summary>Descend into sub-directories.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Glob patterns of names or relative paths to skip. A pattern without '/' matches the entry name ("*.o", ".git");
    /// with '/' it matches the relative path with '/' separators ("build/**", "src/*.tmp"). Supports '*', '?' and '**'.
    /// </summary>
    public IReadOnlyList<string> IgnorePatterns { get; init; } = [".git", ".hg", ".svn"];

    /// <summary>How files are compared.</summary>
    public DirectoryCompareMode Mode { get; init; } = DirectoryCompareMode.Content;

    /// <summary>How symbolic links are handled.</summary>
    public SymlinkHandling Symlinks { get; init; } = SymlinkHandling.CompareTargets;

    /// <summary>Tolerance for last-write time comparisons (FAT has 2-second resolution).</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Match names case-insensitively across sides.</summary>
    public bool IgnoreCaseInNames { get; init; }

    /// <summary>Maximum number of files compared concurrently.</summary>
    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>An entry as found on one side.</summary>
/// <param name="Kind">What exists.</param>
/// <param name="FullPath">The full path (<see langword="null"/> when missing).</param>
/// <param name="Size">Size in bytes for files.</param>
/// <param name="LastWriteTimeUtc">Last write time.</param>
/// <param name="LinkTarget">Link target for symlinks.</param>
public sealed record DirectoryEntrySide(EntryKind Kind, string? FullPath, long Size, DateTime LastWriteTimeUtc, string? LinkTarget = null)
{
    /// <summary>The missing entry.</summary>
    public static DirectoryEntrySide Missing { get; } = new(EntryKind.Missing, null, 0, default);
}

/// <summary>A node of the comparison tree.</summary>
public sealed class DirectoryComparisonNode
{
    /// <summary>Creates a node.</summary>
    public DirectoryComparisonNode(string name, string relativePath, IReadOnlyList<DirectoryEntrySide> sides, EntryStatus status, SidesEqual equalSides, IReadOnlyList<DirectoryComparisonNode> children, string? error = null)
    {
        Name = name;
        RelativePath = relativePath;
        Sides = sides;
        Status = status;
        EqualSides = equalSides;
        Children = children;
        Error = error;
    }

    /// <summary>Entry name ("" for the root).</summary>
    public string Name { get; }

    /// <summary>Path relative to the roots, with '/' separators ("" for the root).</summary>
    public string RelativePath { get; }

    /// <summary>One element per compared root.</summary>
    public IReadOnlyList<DirectoryEntrySide> Sides { get; }

    /// <summary>The status.</summary>
    public EntryStatus Status { get; }

    /// <summary>Which pairs of sides are equal.</summary>
    public SidesEqual EqualSides { get; }

    /// <summary>Children sorted directories first, then by name (ordinal).</summary>
    public IReadOnlyList<DirectoryComparisonNode> Children { get; }

    /// <summary>Error text for <see cref="EntryStatus.Error"/>.</summary>
    public string? Error { get; }

    /// <summary>Whether the entry is a directory on every side where it exists.</summary>
    public bool IsDirectory => Sides.All(s => s.Kind is EntryKind.Directory or EntryKind.Missing) && Sides.Any(s => s.Kind == EntryKind.Directory);

    /// <summary>Enumerates this node and all descendants depth-first.</summary>
    public IEnumerable<DirectoryComparisonNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (DirectoryComparisonNode child in Children)
        {
            foreach (DirectoryComparisonNode d in child.DescendantsAndSelf())
            {
                yield return d;
            }
        }
    }
}

/// <summary>Progress of a running comparison.</summary>
/// <param name="EntriesScanned">Entries found so far.</param>
/// <param name="FilesCompared">Files whose content/metadata comparison finished.</param>
/// <param name="CurrentPath">Relative path being processed.</param>
public sealed record DirectoryCompareProgress(int EntriesScanned, int FilesCompared, string? CurrentPath);

/// <summary>The result of a directory comparison.</summary>
/// <param name="Roots">The compared root directories.</param>
/// <param name="Root">The tree.</param>
public sealed record DirectoryComparisonResult(IReadOnlyList<string> Roots, DirectoryComparisonNode Root)
{
    /// <summary>Counts of file (non-directory) entries per status.</summary>
    public IReadOnlyDictionary<EntryStatus, int> CountFilesByStatus()
        => Root.DescendantsAndSelf().Where(n => !n.IsDirectory && n != Root).GroupBy(n => n.Status).ToDictionary(g => g.Key, g => g.Count());
}

/// <summary>Compares two or three directory trees.</summary>
public static class DirectoryComparer
{
    /// <summary>
    /// Compares the given roots (2 or 3 directories). Runs on the thread pool; reports progress; honours cancellation
    /// by throwing <see cref="OperationCanceledException"/>. Per-entry I/O errors are recorded as
    /// <see cref="EntryStatus.Error"/> nodes and do not abort the comparison.
    /// </summary>
    public static Task<DirectoryComparisonResult> CompareAsync(IReadOnlyList<string> roots, DirectoryCompareOptions? options = null, IProgress<DirectoryCompareProgress>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>Compares two files according to <paramref name="options"/> (true when equal).</summary>
    public static Task<bool> FilesEqualAsync(string pathA, string pathB, DirectoryCompareOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
