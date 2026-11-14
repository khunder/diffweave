namespace DiffWeave.Core.Diff;

/// <summary>The classification of an alignment row.</summary>
public enum RowKind : byte
{
    /// <summary>All panes show the same line (equal under the diff options).</summary>
    Equal,

    /// <summary>The panes show different lines (two-way: both sides present; three-way: a non-conflicting change).</summary>
    Changed,

    /// <summary>Two-way: the line exists only in the second pane (B). Filler in A.</summary>
    Added,

    /// <summary>Two-way: the line exists only in the first pane (A). Filler in B.</summary>
    Removed,

    /// <summary>Three-way: left and right changed the base differently.</summary>
    Conflict,

    /// <summary>The lines differ only in whitespace (or the row only adds/removes blank lines that are ignored).</summary>
    WhitespaceOnly,
}

/// <summary>A contiguous run of non-equal rows, the unit of next/previous-difference navigation.</summary>
/// <param name="Index">The group's index in <see cref="LineAlignment.DifferenceGroups"/>.</param>
/// <param name="StartRow">First row.</param>
/// <param name="RowCount">Number of rows.</param>
/// <param name="Kind">
/// <see cref="RowKind.Conflict"/> if any row is a conflict; otherwise <see cref="RowKind.WhitespaceOnly"/>,
/// <see cref="RowKind.Added"/> or <see cref="RowKind.Removed"/> when all rows have that kind; otherwise <see cref="RowKind.Changed"/>.
/// </param>
/// <param name="IsWhitespaceOnly">Whether every row of the group is <see cref="RowKind.WhitespaceOnly"/>.</param>
public readonly record struct DifferenceGroup(int Index, int StartRow, int RowCount, RowKind Kind, bool IsWhitespaceOnly)
{
    /// <summary>One past the last row.</summary>
    public int EndRow => StartRow + RowCount;
}

/// <summary>
/// Rows for drawing N panes side by side. Each row shows, for every pane, either one line of that pane or a filler
/// (an empty placeholder keeping the panes aligned). Lines of each pane appear in order, each exactly once.
/// </summary>
/// <remarks>Instances are immutable and thread-safe. Memory is O(rows × panes) integers.</remarks>
public abstract class LineAlignment
{
    /// <summary>Value returned by <see cref="GetLine"/> for filler cells.</summary>
    public const int Filler = -1;

    private readonly int[][] _lines;        // [pane][row] -> line or -1
    private readonly int[][] _rowOfLine;    // [pane][line] -> row
    private readonly RowKind[] _kinds;
    private readonly DifferenceGroup[] _groups;

    /// <summary>Initializes the alignment from per-pane row columns.</summary>
    /// <param name="columns">For each pane, the line shown in each row or <see cref="Filler"/>; all columns have the same length.</param>
    /// <param name="kinds">The kind of each row.</param>
    /// <param name="blockIds">
    /// Optional block id per row; difference groups never span rows with different block ids (used to keep separate
    /// merge regions in separate groups). <see langword="null"/> means groups are maximal runs of non-equal rows.
    /// </param>
    protected LineAlignment(int[][] columns, RowKind[] kinds, int[]? blockIds)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(kinds);
        _lines = columns;
        _kinds = kinds;
        _rowOfLine = new int[columns.Length][];
        for (int p = 0; p < columns.Length; p++)
        {
            int[] col = columns[p];
            if (col.Length != kinds.Length)
            {
                throw new ArgumentException("All columns must have one entry per row.", nameof(columns));
            }

            int count = 0;
            foreach (int l in col)
            {
                if (l >= 0)
                {
                    count++;
                }
            }

            var map = new int[count];
            for (int r = 0; r < col.Length; r++)
            {
                if (col[r] >= 0)
                {
                    map[col[r]] = r;
                }
            }

            _rowOfLine[p] = map;
        }

        _groups = BuildGroups(kinds, blockIds);
    }

    /// <summary>Number of panes.</summary>
    public int PaneCount => _lines.Length;

    /// <summary>Number of rows.</summary>
    public int RowCount => _kinds.Length;

    /// <summary>The difference groups in row order.</summary>
    public IReadOnlyList<DifferenceGroup> DifferenceGroups => _groups;

    /// <summary>Number of lines of a pane.</summary>
    public int GetLineCount(int pane) => _rowOfLine[pane].Length;

    /// <summary>The line of <paramref name="pane"/> shown in <paramref name="row"/>, or <see cref="Filler"/>.</summary>
    public int GetLine(int row, int pane) => _lines[pane][row];

    /// <summary>The kind of a row.</summary>
    public RowKind GetRowKind(int row) => _kinds[row];

    /// <summary>The row showing line <paramref name="line"/> of <paramref name="pane"/>. A line equal to the line count maps to <see cref="RowCount"/>.</summary>
    public int GetRow(int pane, int line)
    {
        int[] map = _rowOfLine[pane];
        if (line >= map.Length)
        {
            return RowCount;
        }

        return map[Math.Max(line, 0)];
    }

    /// <summary>
    /// The line of <paramref name="pane"/> at or after <paramref name="row"/> (for filler rows: the next real line),
    /// or the pane's line count when there is none. Useful to map a caret position through filler rows.
    /// </summary>
    public int GetNearestLine(int row, int pane)
    {
        int[] col = _lines[pane];
        for (int r = Math.Max(row, 0); r < col.Length; r++)
        {
            if (col[r] >= 0)
            {
                return col[r];
            }
        }

        return GetLineCount(pane);
    }

    /// <summary>The group containing <paramref name="row"/>, or <see langword="null"/>.</summary>
    public DifferenceGroup? FindGroupAt(int row)
    {
        int i = UpperBound(row) - 1;
        return i >= 0 && row < _groups[i].EndRow ? _groups[i] : null;
    }

    /// <summary>The first group starting after <paramref name="row"/>.</summary>
    /// <param name="row">The current row (e.g. the caret row); use -1 to find the first group.</param>
    /// <param name="skipWhitespaceOnly">Skip whitespace-only groups.</param>
    public DifferenceGroup? FindNextGroup(int row, bool skipWhitespaceOnly = false)
    {
        for (int i = UpperBound(row); i < _groups.Length; i++)
        {
            if (!skipWhitespaceOnly || !_groups[i].IsWhitespaceOnly)
            {
                return _groups[i];
            }
        }

        return null;
    }

    /// <summary>The last group ending before <paramref name="row"/> and not containing it.</summary>
    public DifferenceGroup? FindPreviousGroup(int row, bool skipWhitespaceOnly = false)
    {
        for (int i = UpperBound(row - 1) - 1; i >= 0; i--)
        {
            DifferenceGroup g = _groups[i];
            if (g.StartRow < row && !(row >= g.StartRow && row < g.EndRow) && (!skipWhitespaceOnly || !g.IsWhitespaceOnly))
            {
                return g;
            }
        }

        return null;
    }

    // Index of the first group with StartRow > row.
    private int UpperBound(int row)
    {
        int lo = 0, hi = _groups.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_groups[mid].StartRow <= row)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static DifferenceGroup[] BuildGroups(RowKind[] kinds, int[]? blockIds)
    {
        var groups = new List<DifferenceGroup>();
        int r = 0;
        while (r < kinds.Length)
        {
            if (kinds[r] == RowKind.Equal)
            {
                r++;
                continue;
            }

            int start = r;
            bool conflict = false, allWs = true, allAdded = true, allRemoved = true;
            while (r < kinds.Length && kinds[r] != RowKind.Equal && (r == start || blockIds is null || blockIds[r] == blockIds[start]))
            {
                RowKind k = kinds[r];
                conflict |= k == RowKind.Conflict;
                allWs &= k == RowKind.WhitespaceOnly;
                allAdded &= k == RowKind.Added;
                allRemoved &= k == RowKind.Removed;
                r++;
            }

            RowKind kind = conflict ? RowKind.Conflict
                : allWs ? RowKind.WhitespaceOnly
                : allAdded ? RowKind.Added
                : allRemoved ? RowKind.Removed
                : RowKind.Changed;
            groups.Add(new DifferenceGroup(groups.Count, start, r - start, kind, allWs));
        }

        return [.. groups];
    }
}
