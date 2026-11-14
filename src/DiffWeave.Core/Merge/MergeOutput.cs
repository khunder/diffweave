using DiffWeave.Core.Diff;

namespace DiffWeave.Core.Merge;

/// <summary>
/// Alignment of the three merge inputs and the output: pane 0 = base, 1 = left, 2 = right, 3 = output.
/// Every region occupies a contiguous block of rows (at least one row for any non-empty region and for every
/// unresolved region); difference groups never span two regions.
/// </summary>
public sealed class MergeAlignment : LineAlignment
{
    /// <summary>Index of the base pane.</summary>
    public const int BasePane = 0;

    /// <summary>Index of the left pane.</summary>
    public const int LeftPane = 1;

    /// <summary>Index of the right pane.</summary>
    public const int RightPane = 2;

    /// <summary>Index of the output pane.</summary>
    public const int OutputPane = 3;

    private readonly int[] _regionOfRow;
    private readonly int[] _firstRowOfRegion;
    private readonly bool[] _unresolved;

    internal MergeAlignment(int[][] columns, RowKind[] kinds, int[] regionOfRow, int[] firstRowOfRegion, bool[] unresolvedRegions)
        : base(columns, kinds, regionOfRow)
    {
        _regionOfRow = regionOfRow;
        _firstRowOfRegion = firstRowOfRegion;
        _unresolved = unresolvedRegions;
    }

    /// <summary>The region a row belongs to.</summary>
    public int GetRegion(int row) => _regionOfRow[row];

    /// <summary>The first row of a region (equal to the next region's first row when the region has no rows).</summary>
    public int GetFirstRow(int region) => _firstRowOfRegion[region];

    /// <summary>Number of rows of a region.</summary>
    public int GetRowCount(int region) => (region + 1 < _firstRowOfRegion.Length ? _firstRowOfRegion[region + 1] : RowCount) - _firstRowOfRegion[region];

    /// <summary>
    /// Whether the output cell of <paramref name="row"/> is the placeholder of an unresolved region (the UI draws
    /// e.g. "&lt;unresolved conflict&gt;" there). True only for the first row of an unresolved region.
    /// </summary>
    public bool IsUnresolvedPlaceholder(int row) => _unresolved[_regionOfRow[row]] && _firstRowOfRegion[_regionOfRow[row]] == row;
}

/// <summary>An immutable rendering of a merge session's current output.</summary>
public sealed class MergeOutput
{
    private readonly int[] _regionOfLine;
    private readonly LineRange[] _regionRanges;

    internal MergeOutput(IReadOnlyList<string> lines, int[] regionOfLine, LineRange[] regionRanges, MergeAlignment alignment)
    {
        Lines = lines;
        _regionOfLine = regionOfLine;
        _regionRanges = regionRanges;
        Alignment = alignment;
    }

    /// <summary>The output lines (unresolved regions contribute none).</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The four-pane alignment for drawing.</summary>
    public MergeAlignment Alignment { get; }

    /// <summary>The region that produced output line <paramref name="line"/>.</summary>
    public int GetRegionOfLine(int line) => _regionOfLine[line];

    /// <summary>The output lines produced by a region (an empty range at the region's position when it produced none).</summary>
    public LineRange GetOutputRange(int region) => _regionRanges[region];
}
