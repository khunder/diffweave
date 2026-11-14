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
/// <remarks>
/// The trees are scanned first (sequentially, honouring ignore patterns and symlink handling); then file comparisons
/// run in parallel (<see cref="DirectoryCompareOptions.MaxParallelism"/>); finally statuses are aggregated bottom-up.
/// Content comparison checks sizes first and then compares the bytes in 64 KiB blocks, stopping at the first difference.
/// </remarks>
public static class DirectoryComparer
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Compares the given roots (2 or 3 directories). Runs on the thread pool; reports progress; honours cancellation
    /// by throwing <see cref="OperationCanceledException"/>. Per-entry I/O errors are recorded as
    /// <see cref="EntryStatus.Error"/> nodes and do not abort the comparison.
    /// </summary>
    public static Task<DirectoryComparisonResult> CompareAsync(IReadOnlyList<string> roots, DirectoryCompareOptions? options = null, IProgress<DirectoryCompareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count is < 2 or > 3)
        {
            throw new ArgumentException("Two or three directories are required.", nameof(roots));
        }

        options ??= DirectoryCompareOptions.Default;
        string[] full = [.. roots.Select(Path.GetFullPath)];
        foreach (string r in full)
        {
            if (!Directory.Exists(r))
            {
                throw new DirectoryNotFoundException($"Directory not found: {r}");
            }
        }

        return Task.Run(() => new Run(full, options, progress, cancellationToken).ExecuteAsync(), cancellationToken);
    }

    /// <summary>Compares two files according to <paramref name="options"/> (true when equal).</summary>
    public static async Task<bool> FilesEqualAsync(string pathA, string pathB, DirectoryCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DirectoryCompareOptions.Default;
        var a = new FileInfo(pathA);
        var b = new FileInfo(pathB);
        return await FilesEqualAsync(a.FullName, a.Length, a.LastWriteTimeUtc, b.FullName, b.Length, b.LastWriteTimeUtc, options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> FilesEqualAsync(string pa, long sa, DateTime ta, string pb, long sb, DateTime tb, DirectoryCompareOptions options, CancellationToken ct)
    {
        if (sa != sb)
        {
            return false;
        }

        bool timeEqual = (ta - tb).Duration() <= options.TimeTolerance;
        if (options.Mode == DirectoryCompareMode.SizeAndTime)
        {
            return timeEqual;
        }

        if (options.Mode.HasFlag(DirectoryCompareMode.SizeAndTime) && timeEqual)
        {
            return true;
        }

        if (string.Equals(pa, pb, StringComparison.Ordinal))
        {
            return true;
        }

        await using var fa = new FileStream(pa, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        await using var fb = new FileStream(pb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        byte[] ba = new byte[BufferSize];
        byte[] bb = new byte[BufferSize];
        while (true)
        {
            int na = await fa.ReadAtLeastAsync(ba, BufferSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            int nb = await fb.ReadAtLeastAsync(bb, BufferSize, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb)))
            {
                return false;
            }

            if (na < BufferSize)
            {
                return true;
            }
        }
    }

    /// <summary>Converts a glob to a regex: '**' any characters, '*' any except '/', '?' one except '/'.</summary>
    internal static System.Text.RegularExpressions.Regex GlobToRegex(string glob, bool ignoreCase)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        var opts = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        if (ignoreCase)
        {
            opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        }

        return new System.Text.RegularExpressions.Regex(sb.ToString(), opts);
    }

    private sealed class Builder(string name, string relativePath, DirectoryEntrySide[] sides)
    {
        public string Name { get; } = name;

        public string RelativePath { get; } = relativePath;

        public DirectoryEntrySide[] Sides { get; } = sides;

        public List<Builder> Children { get; } = [];

        public EntryStatus? Status { get; set; }

        public SidesEqual EqualSides { get; set; }

        public string? Error { get; set; }
    }

    private sealed class Run
    {
        private readonly string[] _roots;
        private readonly DirectoryCompareOptions _options;
        private readonly IProgress<DirectoryCompareProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly List<(System.Text.RegularExpressions.Regex Regex, bool ByPath)> _ignore;
        private readonly List<Builder> _fileJobs = [];
        private int _scanned;
        private int _compared;

        public Run(string[] roots, DirectoryCompareOptions options, IProgress<DirectoryCompareProgress>? progress, CancellationToken ct)
        {
            _roots = roots;
            _options = options;
            _progress = progress;
            _ct = ct;
            bool ic = options.IgnoreCaseInNames || OperatingSystem.IsWindows();
            _ignore = [.. options.IgnorePatterns.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => (GlobToRegex(p.Trim().TrimEnd('/'), ic), p.Trim().TrimEnd('/').Contains('/', StringComparison.Ordinal)))];
        }

        public async Task<DirectoryComparisonResult> ExecuteAsync()
        {
            DirectoryEntrySide[] rootSides = [.. _roots.Select(r => Describe(new DirectoryInfo(r)))];
            var root = new Builder(string.Empty, string.Empty, rootSides);
            var visited = _roots.Select(_ => new HashSet<string>(StringComparer.Ordinal)).ToArray();
            Scan(root, _roots.Select(r => (string?)r).ToArray(), visited);

            await Parallel.ForEachAsync(_fileJobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism), CancellationToken = _ct }, async (job, ct) =>
            {
                try
                {
                    job.EqualSides = await CompareFiles(job.Sides, ct).ConfigureAwait(false);
                    job.Status = AllEqual(job.EqualSides, job.Sides.Length) ? EntryStatus.Identical : EntryStatus.Different;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    job.Status = EntryStatus.Error;
                    job.Error = ex.Message;
                }

                int done = Interlocked.Increment(ref _compared);
                if ((done & 0x3F) == 0)
                {
                    _progress?.Report(new DirectoryCompareProgress(_scanned, done, job.RelativePath));
                }
            }).ConfigureAwait(false);

            _progress?.Report(new DirectoryCompareProgress(_scanned, _compared, null));
            return new DirectoryComparisonResult(_roots, Freeze(root));
        }

        private static bool AllEqual(SidesEqual eq, int n) => n == 2 ? eq.HasFlag(SidesEqual.AB) : eq == (SidesEqual.AB | SidesEqual.AC | SidesEqual.BC);

        private async Task<SidesEqual> CompareFiles(DirectoryEntrySide[] s, CancellationToken ct)
        {
            async Task<bool> Eq(int i, int j)
            {
                if (s[i].Kind == EntryKind.Symlink || s[j].Kind == EntryKind.Symlink)
                {
                    return s[i].Kind == s[j].Kind && s[i].LinkTarget == s[j].LinkTarget;
                }

                return await FilesEqualAsync(s[i].FullPath!, s[i].Size, s[i].LastWriteTimeUtc, s[j].FullPath!, s[j].Size, s[j].LastWriteTimeUtc, _options, ct).ConfigureAwait(false);
            }

            SidesEqual result = SidesEqual.None;
            if (await Eq(0, 1).ConfigureAwait(false))
            {
                result |= SidesEqual.AB;
            }

            if (s.Length == 3)
            {
                bool ac = await Eq(0, 2).ConfigureAwait(false);
                if (ac)
                {
                    result |= SidesEqual.AC;
                }

                bool bc = result.HasFlag(SidesEqual.AB) ? ac : (ac ? false : await Eq(1, 2).ConfigureAwait(false));
                if (bc)
                {
                    result |= SidesEqual.BC;
                }
            }

            return result;
        }

        private bool IsIgnored(string name, string relativePath)
        {
            foreach ((System.Text.RegularExpressions.Regex regex, bool byPath) in _ignore)
            {
                if (regex.IsMatch(byPath ? relativePath : name))
                {
                    return true;
                }
            }

            return false;
        }

        private DirectoryEntrySide Describe(FileSystemInfo info)
        {
            try
            {
                string? target = info.LinkTarget;
                if (target is not null)
                {
                    switch (_options.Symlinks)
                    {
                        case SymlinkHandling.CompareTargets:
                            return new DirectoryEntrySide(EntryKind.Symlink, info.FullName, 0, info.LastWriteTimeUtc, target);
                        case SymlinkHandling.Follow:
                            FileSystemInfo? final = info.ResolveLinkTarget(returnFinalTarget: true);
                            if (final is null || !final.Exists)
                            {
                                return new DirectoryEntrySide(EntryKind.Other, info.FullName, 0, info.LastWriteTimeUtc, target);
                            }

                            return final is DirectoryInfo
                                ? new DirectoryEntrySide(EntryKind.Directory, info.FullName, 0, final.LastWriteTimeUtc)
                                : new DirectoryEntrySide(EntryKind.File, info.FullName, ((FileInfo)final).Length, final.LastWriteTimeUtc);
                        default:
                            return DirectoryEntrySide.Missing;
                    }
                }

                return info switch
                {
                    DirectoryInfo d => new DirectoryEntrySide(EntryKind.Directory, d.FullName, 0, d.LastWriteTimeUtc),
                    FileInfo f when (f.Attributes & FileAttributes.Device) == 0 => new DirectoryEntrySide(EntryKind.File, f.FullName, f.Length, f.LastWriteTimeUtc),
                    _ => new DirectoryEntrySide(EntryKind.Other, info.FullName, 0, info.LastWriteTimeUtc),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DirectoryEntrySide(EntryKind.Other, info.FullName, 0, default);
            }
        }

        private void Scan(Builder node, string?[] dirs, HashSet<string>[] visited)
        {
            _ct.ThrowIfCancellationRequested();
            StringComparer cmp = _options.IgnoreCaseInNames ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var byName = new Dictionary<string, (string Name, FileSystemInfo?[] Infos)>(cmp);
            for (int side = 0; side < dirs.Length; side++)
            {
                if (dirs[side] is not { } dir)
                {
                    continue;
                }

                // Cycle detection when following links.
                string real = RealPath(dir);
                if (!visited[side].Add(real))
                {
                    node.Status = EntryStatus.Error;
                    node.Error = $"Symbolic link cycle at '{dir}'.";
                    return;
                }

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false });
                    foreach (FileSystemInfo info in entries)
                    {
                        string rel = node.RelativePath.Length == 0 ? info.Name : node.RelativePath + "/" + info.Name;
                        if (IsIgnored(info.Name, rel))
                        {
                            continue;
                        }

                        if (!byName.TryGetValue(info.Name, out var slot))
                        {
                            slot = (info.Name, new FileSystemInfo?[dirs.Length]);
                            byName.Add(info.Name, slot);
                        }

                        slot.Infos[side] = info;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    node.Status = EntryStatus.Error;
                    node.Error = ex.Message;
                }
            }

            foreach ((string name, FileSystemInfo?[] infos) in byName.Values)
            {
                _ct.ThrowIfCancellationRequested();
                DirectoryEntrySide[] sides = [.. infos.Select(i => i is null ? DirectoryEntrySide.Missing : Describe(i))];
                if (sides.All(s => s.Kind == EntryKind.Missing))
                {
                    continue; // skipped symlinks
                }

                string rel = node.RelativePath.Length == 0 ? name : node.RelativePath + "/" + name;
                var child = new Builder(name, rel, sides);
                node.Children.Add(child);
                if (Interlocked.Increment(ref _scanned) % 256 == 0)
                {
                    _progress?.Report(new DirectoryCompareProgress(_scanned, _compared, rel));
                }

                EntryKind[] present = [.. sides.Where(s => s.Kind != EntryKind.Missing).Select(s => s.Kind).Distinct()];
                if (present.Length > 1)
                {
                    child.Status = EntryStatus.TypeMismatch;
                    continue;
                }

                if (present[0] == EntryKind.Directory)
                {
                    if (_options.Recursive)
                    {
                        Scan(child, [.. sides.Select(s => s.Kind == EntryKind.Directory ? s.FullPath : null)], visited);
                    }
                }
                else if (sides.All(s => s.Kind != EntryKind.Missing))
                {
                    if (present[0] is EntryKind.File or EntryKind.Symlink)
                    {
                        _fileJobs.Add(child);
                    }
                    else
                    {
                        child.Status = EntryStatus.Error;
                        child.Error = "Not a regular file.";
                    }
                }
            }

            for (int side = 0; side < dirs.Length; side++)
            {
                if (dirs[side] is { } dir)
                {
                    visited[side].Remove(RealPath(dir));
                }
            }
        }

        private static string RealPath(string dir)
        {
            try
            {
                var info = new DirectoryInfo(dir);
                FileSystemInfo? target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
                return Path.TrimEndingDirectorySeparator((target ?? info).FullName);
            }
            catch (IOException)
            {
                return dir;
            }
        }

        private DirectoryComparisonNode Freeze(Builder b)
        {
            DirectoryComparisonNode[] children = [.. b.Children
                .Select(Freeze)
                .OrderBy(c => c.IsDirectory ? 0 : 1)
                .ThenBy(c => c.Name, StringComparer.Ordinal)];
            EntryStatus status = b.Status ?? PresenceStatus(b.Sides) ?? (children.All(c => c.Status == EntryStatus.Identical) ? EntryStatus.Identical : EntryStatus.Different);
            SidesEqual eq = b.EqualSides;
            if (b.Status is null && status == EntryStatus.Identical)
            {
                eq = b.Sides.Length == 2 ? SidesEqual.AB : SidesEqual.AB | SidesEqual.AC | SidesEqual.BC;
            }

            return new DirectoryComparisonNode(b.Name, b.RelativePath, b.Sides, status, eq, children, b.Error);
        }

        private static EntryStatus? PresenceStatus(DirectoryEntrySide[] sides)
        {
            bool[] p = [.. sides.Select(s => s.Kind != EntryKind.Missing)];
            if (p.All(x => x))
            {
                return null;
            }

            if (sides.Length == 2)
            {
                return p[0] ? EntryStatus.OnlyInA : EntryStatus.OnlyInB;
            }

            int count = p.Count(x => x);
            if (count == 1)
            {
                return p[0] ? EntryStatus.OnlyInA : p[1] ? EntryStatus.OnlyInB : EntryStatus.OnlyInC;
            }

            return !p[0] ? EntryStatus.MissingInA : !p[1] ? EntryStatus.MissingInB : EntryStatus.MissingInC;
        }
    }
}
