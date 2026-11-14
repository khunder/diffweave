using DiffWeave.Core.Diff;

namespace DiffWeave.App.Editor;

/// <summary>A difference block: one span per pane plus its kind. The unit for bands, ruler and navigation.</summary>
/// <param name="Index">Index of the block (difference group index).</param>
/// <param name="StartRow">First alignment row.</param>
/// <param name="RowCount">Number of alignment rows.</param>
/// <param name="Spans">Line span of the block in each pane.</param>
/// <param name="Kind">Highlight kind used for the block in bands and the ruler.</param>
/// <param name="IsConflict">True for merge conflicts.</param>
/// <param name="IsWhitespaceOnly">True when the block only differs in whitespace.</param>
public sealed record DiffBlock(int Index, int StartRow, int RowCount, IReadOnlyList<LineSpan> Spans, HighlightKind Kind, bool IsConflict, bool IsWhitespaceOnly)
{
    /// <summary>The kind used for a specific pane (e.g. base pane of a three-way change).</summary>
    public IReadOnlyList<HighlightKind>? PaneKinds { get; init; }

    /// <summary>The kind for <paramref name="pane"/>.</summary>
    public HighlightKind KindFor(int pane) => PaneKinds is { } k && pane < k.Count ? k[pane] : Kind;
}

/// <summary>
/// Maps lines between panes that display different documents, using the row alignment computed by the core.
/// Panes show only real lines (no filler rows); the alignment rows are used as a common coordinate system for
/// synchronized scrolling, connecting bands and the overview ruler.
/// </summary>
public sealed class PaneMapping
{
    private readonly int[][] _linesBeforeRow; // [pane][row] -> number of real lines of pane in rows [0,row)
    private readonly int[][] _rowOfLine;      // [pane][line] -> row

    /// <summary>Builds the mapping from an alignment.</summary>
    public PaneMapping(LineAlignment alignment, Func<DifferenceGroup, int[]?, (HighlightKind Kind, IReadOnlyList<HighlightKind>? PaneKinds)>? classify = null, IReadOnlyList<int>? panes = null)
    {
        Alignment = alignment;
        int[] paneIndexes = panes is null ? [.. Enumerable.Range(0, alignment.PaneCount)] : [.. panes];
        PaneCount = paneIndexes.Length;
        RowCount = alignment.RowCount;
        _linesBeforeRow = new int[PaneCount][];
        _rowOfLine = new int[PaneCount][];
        for (int i = 0; i < PaneCount; i++)
        {
            int pane = paneIndexes[i];
            int[] before = new int[RowCount + 1];
            int[] rowOf = new int[alignment.GetLineCount(pane)];
            for (int r = 0; r < RowCount; r++)
            {
                int line = alignment.GetLine(r, pane);
                before[r + 1] = before[r] + (line >= 0 ? 1 : 0);
                if (line >= 0)
                {
                    rowOf[line] = r;
                }
            }

            _linesBeforeRow[i] = before;
            _rowOfLine[i] = rowOf;
        }

        List<DiffBlock> blocks = [];
        foreach (DifferenceGroup g in alignment.DifferenceGroups)
        {
            LineSpan[] spans = new LineSpan[PaneCount];
            for (int i = 0; i < PaneCount; i++)
            {
                int start = _linesBeforeRow[i][g.StartRow];
                int end = _linesBeforeRow[i][g.EndRow];
                spans[i] = new LineSpan(start, end - start);
            }

            (HighlightKind kind, IReadOnlyList<HighlightKind>? paneKinds) = classify?.Invoke(g, paneIndexes) ?? (DefaultKind(g.Kind), null);
            blocks.Add(new DiffBlock(blocks.Count, g.StartRow, g.RowCount, spans, kind, g.Kind == RowKind.Conflict, g.IsWhitespaceOnly) { PaneKinds = paneKinds });
        }

        Blocks = blocks;
    }

    /// <summary>The source alignment.</summary>
    public LineAlignment Alignment { get; }

    /// <summary>Number of panes.</summary>
    public int PaneCount { get; }

    /// <summary>Number of alignment rows.</summary>
    public int RowCount { get; }

    /// <summary>The difference blocks in order.</summary>
    public IReadOnlyList<DiffBlock> Blocks { get; }

    /// <summary>Number of lines of a pane.</summary>
    public int LineCount(int pane) => _rowOfLine[pane].Length;

    /// <summary>Default classification of a group kind.</summary>
    public static HighlightKind DefaultKind(RowKind kind) => kind switch
    {
        RowKind.Added => HighlightKind.Inserted,
        RowKind.Removed => HighlightKind.Deleted,
        RowKind.Conflict => HighlightKind.Conflict,
        RowKind.Equal => HighlightKind.None,
        _ => HighlightKind.Changed,
    };

    /// <summary>Converts a fractional line of a pane to a fractional alignment row.</summary>
    public double LineToRow(int pane, double line)
    {
        int[] rows = _rowOfLine[pane];
        if (rows.Length == 0)
        {
            return 0;
        }

        if (line <= 0)
        {
            return rows[0] + line;
        }

        int whole = (int)Math.Floor(line);
        if (whole >= rows.Length)
        {
            return RowCount + (line - rows.Length);
        }

        double frac = line - whole;
        int next = whole + 1 < rows.Length ? rows[whole + 1] : RowCount;
        return rows[whole] + (frac * (next - rows[whole]));
    }

    /// <summary>Converts a fractional alignment row to a fractional line of a pane (filler rows map to the next line).</summary>
    public double RowToLine(int pane, double row)
    {
        int[] before = _linesBeforeRow[pane];
        if (row <= 0)
        {
            return 0;
        }

        if (row >= RowCount)
        {
            return before[RowCount] + (row - RowCount);
        }

        int whole = (int)Math.Floor(row);
        double frac = row - whole;
        bool real = before[whole + 1] > before[whole];
        return before[whole] + (real ? frac : 0);
    }

    /// <summary>Maps a fractional line from one pane to another.</summary>
    public double MapLine(int fromPane, int toPane, double line) => fromPane == toPane ? line : RowToLine(toPane, LineToRow(fromPane, line));

    /// <summary>The block containing a line of a pane, or the block positioned at it (empty spans), or null.</summary>
    public DiffBlock? FindBlockAtLine(int pane, int line)
    {
        DiffBlock? best = null;
        foreach (DiffBlock b in Blocks)
        {
            LineSpan s = b.Spans[pane];
            if (s.Contains(line) || (s.Count == 0 && s.Start == line))
            {
                return b;
            }

            if (s.Start > line)
            {
                break;
            }

            best = null;
        }

        return best;
    }

    /// <summary>The first block starting after the given line of a pane.</summary>
    public DiffBlock? FindNextBlock(int pane, int line, Func<DiffBlock, bool>? filter = null)
    {
        foreach (DiffBlock b in Blocks)
        {
            LineSpan s = b.Spans[pane];
            if ((s.Start > line || (s.Count == 0 && s.Start == line + 1)) && (filter is null || filter(b)))
            {
                return b;
            }
        }

        return null;
    }

    /// <summary>The last block ending before the given line of a pane.</summary>
    public DiffBlock? FindPreviousBlock(int pane, int line, Func<DiffBlock, bool>? filter = null)
    {
        for (int i = Blocks.Count - 1; i >= 0; i--)
        {
            DiffBlock b = Blocks[i];
            LineSpan s = b.Spans[pane];
            if (s.Start < line && !s.Contains(line) && (filter is null || filter(b)))
            {
                return b;
            }
        }

        return null;
    }
}
