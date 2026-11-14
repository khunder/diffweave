using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffWeave.App.Editor;
using DiffWeave.Core.Directories;

namespace DiffWeave.App.Documents;

/// <summary>Which rows of a directory comparison are visible.</summary>
public sealed record DirectoryFilter(bool ShowIdentical = true, bool ShowDifferent = true, bool ShowOnlyInOne = true)
{
    /// <summary>Returns true when a file node passes the filter.</summary>
    public bool Accepts(EntryStatus status) => status switch
    {
        EntryStatus.Identical => ShowIdentical,
        EntryStatus.Different or EntryStatus.TypeMismatch or EntryStatus.Error => ShowDifferent,
        _ => ShowOnlyInOne,
    };
}

/// <summary>One visible row of the directory tree.</summary>
public sealed partial class DirectoryRow : ObservableObject
{
    internal DirectoryRow(DirectoryComparisonNode node, int depth, int sideCount)
    {
        Node = node;
        Depth = depth;
        SideCount = sideCount;
    }

    /// <summary>The comparison node.</summary>
    public DirectoryComparisonNode Node { get; }

    /// <summary>Nesting depth (0 = top level).</summary>
    public int Depth { get; }

    /// <summary>Number of compared directories.</summary>
    public int SideCount { get; }

    /// <summary>Whether the directory row is expanded.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Name.</summary>
    public string Name => Node.Name;

    /// <summary>True for directories.</summary>
    public bool IsDirectory => Node.IsDirectory;

    /// <summary>Indentation in pixels.</summary>
    public double Indent => Depth * 16;

    /// <summary>Expander glyph.</summary>
    public string Expander => IsDirectory ? (IsExpanded ? "▾" : "▸") : string.Empty;

    /// <summary>True when a third directory is compared.</summary>
    public bool HasC => SideCount > 2;

    /// <summary>True for files.</summary>
    public bool IsFile => !IsDirectory;

    /// <summary>Status text.</summary>
    public string StatusText => Node.Status switch
    {
        EntryStatus.Identical => "Identical",
        EntryStatus.Different => "Different",
        EntryStatus.OnlyInA => "Only in A",
        EntryStatus.OnlyInB => "Only in B",
        EntryStatus.OnlyInC => "Only in C",
        EntryStatus.MissingInA => "Missing in A",
        EntryStatus.MissingInB => "Missing in B",
        EntryStatus.MissingInC => "Missing in C",
        EntryStatus.TypeMismatch => "Type mismatch",
        _ => "Error",
    };

    /// <summary>Highlight kind of the status (theme brush).</summary>
    public HighlightKind StatusKind => Node.Status switch
    {
        EntryStatus.Identical => HighlightKind.None,
        EntryStatus.Different or EntryStatus.TypeMismatch => HighlightKind.Changed,
        EntryStatus.Error => HighlightKind.Error,
        EntryStatus.OnlyInA or EntryStatus.MissingInB or EntryStatus.MissingInC => HighlightKind.Deleted,
        _ => HighlightKind.Inserted,
    };

    /// <summary>Resource key of the status brush.</summary>
    public string StatusBrushKey => Node.Status switch
    {
        EntryStatus.Identical => "DW.Dir.Same",
        EntryStatus.Different or EntryStatus.TypeMismatch => "DW.Dir.Different",
        EntryStatus.Error => "DW.Dir.Error",
        _ => "DW.Dir.Only",
    };

    /// <summary>Status glyph.</summary>
    public string StatusGlyph => Node.Status switch
    {
        EntryStatus.Identical => "=",
        EntryStatus.Different => "≠",
        EntryStatus.TypeMismatch => "≠",
        EntryStatus.Error => "!",
        _ => "+",
    };

    /// <summary>Size column text for a side.</summary>
    public string SizeOf(int side) => side < Node.Sides.Count && Node.Sides[side].Kind == EntryKind.File ? FormatSize(Node.Sides[side].Size) : Node.Sides.Count > side && Node.Sides[side].Kind == EntryKind.Missing ? "—" : string.Empty;

    /// <summary>Modified column text for a side.</summary>
    public string ModifiedOf(int side) => side < Node.Sides.Count && Node.Sides[side].Kind != EntryKind.Missing && Node.Sides[side].LastWriteTimeUtc != default
        ? Node.Sides[side].LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>Size of A.</summary>
    public string SizeA => SizeOf(0);

    /// <summary>Size of B.</summary>
    public string SizeB => SizeOf(1);

    /// <summary>Size of C.</summary>
    public string SizeC => SizeOf(2);

    /// <summary>Modified time of A.</summary>
    public string ModifiedA => ModifiedOf(0);

    /// <summary>Modified time of B.</summary>
    public string ModifiedB => ModifiedOf(1);

    /// <summary>Modified time of C.</summary>
    public string ModifiedC => ModifiedOf(2);

    /// <summary>True when this is a file that exists on at least two sides.</summary>
    public bool CanOpen => !IsDirectory && Node.Sides.Count(s => s.Kind == EntryKind.File) >= 1;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Expander));

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}

/// <summary>A comparison of two or three directories: flattened, filterable tree of <see cref="DirectoryRow"/>s.</summary>
public sealed class DirectoryDocument
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private DirectoryFilter _filter = new();

    /// <summary>Creates a document for the given roots.</summary>
    public DirectoryDocument(IReadOnlyList<string> roots, IReadOnlyList<string?>? labels = null)
    {
        Roots = [.. roots.Select(Path.GetFullPath)];
        Labels = [.. Roots.Select((r, i) => labels is not null && i < labels.Count && labels[i] is { } l ? l : r)];
    }

    /// <summary>The compared directories.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Display labels.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>The last result.</summary>
    public DirectoryComparisonResult? Result { get; private set; }

    /// <summary>Visible rows.</summary>
    public ObservableCollection<DirectoryRow> Rows { get; } = [];

    /// <summary>The filter.</summary>
    public DirectoryFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            RebuildRows();
        }
    }

    /// <summary>Summary for the status bar.</summary>
    public string Summary
    {
        get
        {
            if (Result is null)
            {
                return "Comparing…";
            }

            List<DirectoryComparisonNode> files = [.. Result.Root.DescendantsAndSelf().Where(n => !n.IsDirectory && n != Result.Root)];
            int same = files.Count(f => f.Status == EntryStatus.Identical);
            int diff = files.Count(f => f.Status is EntryStatus.Different or EntryStatus.TypeMismatch);
            int only = files.Count - same - diff;
            return $"{files.Count} files: {diff} different, {only} only on one side, {same} identical";
        }
    }

    /// <summary>Runs the comparison (on the thread pool) and rebuilds the rows.</summary>
    public async Task CompareAsync(CancellationToken cancellationToken = default)
    {
        DirectoryComparisonResult result = await DirectoryComparer.CompareAsync(Roots, new DirectoryCompareOptions { Mode = DirectoryCompareMode.SizeAndTimeThenContent }, null, cancellationToken).ConfigureAwait(true);
        Result = result;
        if (_expanded.Count == 0)
        {
            foreach (DirectoryComparisonNode child in result.Root.Children)
            {
                if (child.IsDirectory && child.Status != EntryStatus.Identical)
                {
                    _expanded.Add(child.RelativePath);
                }
            }
        }

        RebuildRows();
    }

    /// <summary>Toggles a directory row.</summary>
    public void Toggle(DirectoryRow row)
    {
        if (!row.IsDirectory)
        {
            return;
        }

        if (!_expanded.Remove(row.Node.RelativePath))
        {
            _expanded.Add(row.Node.RelativePath);
        }

        RebuildRows();
    }

    /// <summary>Expands every directory.</summary>
    public void ExpandAll()
    {
        if (Result is null)
        {
            return;
        }

        foreach (DirectoryComparisonNode n in Result.Root.DescendantsAndSelf())
        {
            if (n.IsDirectory)
            {
                _expanded.Add(n.RelativePath);
            }
        }

        RebuildRows();
    }

    /// <summary>Full paths of a node on every side (null when missing).</summary>
    public IReadOnlyList<string?> PathsOf(DirectoryComparisonNode node)
        => [.. node.Sides.Select((s, i) => s.Kind == EntryKind.Missing ? null : s.FullPath ?? Path.Combine(Roots[i], node.RelativePath))];

    /// <summary>
    /// Copies the entry from side <paramref name="from"/> to side <paramref name="to"/> (file or whole directory,
    /// overwriting). Returns the destination path.
    /// </summary>
    public string Copy(DirectoryComparisonNode node, int from, int to)
    {
        string source = PathsOf(node)[from] ?? throw new InvalidOperationException("The entry does not exist on the source side.");
        string dest = Path.Combine(Roots[to], node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(source))
        {
            CopyDirectory(source, dest);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, true);
        }

        return dest;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.GetFiles(source))
        {
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        }

        foreach (string d in Directory.GetDirectories(source))
        {
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (Result is null)
        {
            return;
        }

        foreach (DirectoryComparisonNode child in Result.Root.Children)
        {
            AddRows(child, 0);
        }
    }

    private bool IsVisible(DirectoryComparisonNode node)
        => node.IsDirectory ? node.Children.Any(IsVisible) || (node.Children.Count == 0 && _filter.Accepts(node.Status)) : _filter.Accepts(node.Status);

    private void AddRows(DirectoryComparisonNode node, int depth)
    {
        if (!IsVisible(node))
        {
            return;
        }

        bool expanded = _expanded.Contains(node.RelativePath);
        Rows.Add(new DirectoryRow(node, depth, Roots.Count) { IsExpanded = expanded });
        if (node.IsDirectory && expanded)
        {
            foreach (DirectoryComparisonNode c in node.Children)
            {
                AddRows(c, depth + 1);
            }
        }
    }
}
