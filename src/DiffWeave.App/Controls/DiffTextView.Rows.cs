using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>
/// The visual row model: one row per line, several rows per line with word wrap, plus phantom rows
/// (placeholders such as "unresolved conflict" that are not part of the text).
/// </summary>
public partial class DiffTextView
{
    private readonly Dictionary<int, List<PhantomRow>> _phantoms = [];

    private int[]? _rowPrefix;
    private int _wrapColumns;

    // _rowPrefix[i] = first row of line i's block (its phantom rows, then its text rows);
    // _rowPrefix[LineCount] = first trailing phantom row; _rowPrefix[LineCount + 1] = total rows.

    /// <summary>Replaces the phantom rows (placeholder rows drawn between lines).</summary>
    public void SetPhantomRows(IEnumerable<PhantomRow> rows)
    {
        _phantoms.Clear();
        foreach (PhantomRow r in rows)
        {
            if (!_phantoms.TryGetValue(r.BeforeLine, out List<PhantomRow>? list))
            {
                list = [];
                _phantoms[r.BeforeLine] = list;
            }

            list.Add(r);
        }

        _rowPrefix = null;
        VerticalOffset = _verticalOffset;
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Number of phantom rows.</summary>
    public int PhantomRowCount => _phantoms.Values.Sum(l => l.Count);

    private bool UsesRowMap => WordWrap || _phantoms.Count > 0;

    private int PhantomsBefore(int line) => _phantoms.TryGetValue(line, out List<PhantomRow>? l) ? l.Count : 0;

    /// <summary>Total number of visual rows.</summary>
    public int RowCount
    {
        get
        {
            if (!UsesRowMap)
            {
                return LineCount;
            }

            EnsureRowPrefix();
            return _rowPrefix![^1];
        }
    }

    private int TextRow(int line) => _rowPrefix![line] + PhantomsBefore(line);

    /// <summary>Converts a fractional line to a fractional visual row (the line's first text row).</summary>
    public double LineToRow(double line)
    {
        if (!UsesRowMap)
        {
            return line;
        }

        EnsureRowPrefix();
        int[] p = _rowPrefix!;
        int lines = LineCount;
        if (line <= 0 && lines > 0)
        {
            return TextRow(0) + line;
        }

        int whole = (int)Math.Floor(line);
        if (whole >= lines)
        {
            return p[lines] + (line - lines);
        }

        double frac = line - whole;
        int t = TextRow(whole);
        return t + (frac * (p[whole + 1] - t));
    }

    /// <summary>Converts a fractional visual row to a fractional line.</summary>
    public double RowToLine(double row)
    {
        if (!UsesRowMap)
        {
            return row;
        }

        EnsureRowPrefix();
        int[] p = _rowPrefix!;
        int lines = LineCount;
        if (row >= p[lines])
        {
            return lines + Math.Max(0, row - p[^1]);
        }

        int line = FindBlockOfRow((int)Math.Floor(Math.Max(0, row)));
        int t = TextRow(line);
        if (row < t)
        {
            return line;
        }

        return line + ((row - t) / Math.Max(1, p[line + 1] - t));
    }

    /// <summary>The top and bottom (control coordinates) of a line span; empty spans cover the phantom rows at their position.</summary>
    public (double Top, double Bottom) GetSpanBounds(LineSpan span)
    {
        double rh = RowHeight;
        if (!UsesRowMap)
        {
            double y = (span.Start * rh) - _verticalOffset;
            return (y, y + (span.Count * rh));
        }

        EnsureRowPrefix();
        int lines = LineCount;
        int start = Math.Clamp(span.Start, 0, lines);
        int end = Math.Clamp(span.End, 0, lines);
        double top, bottom;
        if (span.Count == 0)
        {
            top = _rowPrefix![start];
            bottom = start < lines ? TextRow(start) : _rowPrefix[^1];
        }
        else
        {
            top = TextRow(start);
            bottom = _rowPrefix![end];
        }

        return ((top * rh) - _verticalOffset, (bottom * rh) - _verticalOffset);
    }

    private int FindBlockOfRow(int row)
    {
        int[] p = _rowPrefix!;
        int lo = 0, hi = p.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (p[mid] <= row)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private int RowsOfLineText(int line, int tab, int width)
    {
        if (!WordWrap)
        {
            return 1;
        }

        int len = DisplayLength(Source!.GetLine(line), tab);
        return Math.Max(1, (len + width - 1) / width);
    }

    private void EnsureRowPrefix()
    {
        if (_rowPrefix is not null)
        {
            return;
        }

        EnsureMetrics();
        int lines = LineCount;
        int[] p = new int[lines + 2];
        int width = Math.Max(8, (int)Math.Floor((Bounds.Width - TextAreaLeft - TextPadding) / _charWidth));
        _wrapColumns = width;
        int tab = TabSize;
        for (int i = 0; i < lines; i++)
        {
            p[i + 1] = p[i] + PhantomsBefore(i) + RowsOfLineText(i, tab, width);
        }

        p[lines + 1] = p[lines] + PhantomsBefore(lines);
        _rowPrefix = p;
    }

    /// <summary>Computes the visual rows intersecting the viewport.</summary>
    public IReadOnlyList<VisualRow> GetVisibleRows()
    {
        List<VisualRow> rows = [];
        if (Source is null)
        {
            return rows;
        }

        double rh = RowHeight;
        int rowCount = RowCount;
        int firstRow = Math.Max(0, (int)Math.Floor(_verticalOffset / rh));
        int lastRow = Math.Min(rowCount - 1, (int)Math.Ceiling((_verticalOffset + Bounds.Height) / rh));
        if (!UsesRowMap)
        {
            for (int r = firstRow; r <= lastRow; r++)
            {
                rows.Add(new VisualRow(r, r, 0, 0, LineLength(r), (r * rh) - _verticalOffset));
            }

            return rows;
        }

        EnsureRowPrefix();
        int[] p = _rowPrefix!;
        int lines = LineCount;
        int line = FindBlockOfRow(firstRow);
        int r2 = firstRow;
        while (r2 <= lastRow && line <= lines)
        {
            int ph = PhantomsBefore(line);
            int offset = r2 - p[line];
            if (offset < ph)
            {
                PhantomRow phantom = _phantoms[line][offset];
                rows.Add(new VisualRow(r2, line, -1, 0, 0, (r2 * rh) - _verticalOffset) { Phantom = phantom });
                r2++;
                continue;
            }

            if (line == lines)
            {
                break;
            }

            int seg = offset - ph;
            int segments = p[line + 1] - p[line] - ph;
            if (!WordWrap)
            {
                rows.Add(new VisualRow(r2, line, 0, 0, LineLength(line), (r2 * rh) - _verticalOffset));
            }
            else
            {
                DisplayLine d = GetDisplayLine(line);
                int ds = seg * _wrapColumns;
                int de = Math.Min(d.Text.Length, ds + _wrapColumns);
                int sc = d.ToSource(ds);
                int ec = seg == segments - 1 ? LineLength(line) : d.ToSource(de);
                rows.Add(new VisualRow(r2, line, seg, sc, ec, (r2 * rh) - _verticalOffset));
            }

            r2++;
            if (seg + 1 >= segments)
            {
                line++;
            }
        }

        return rows;
    }

    /// <summary>Maps a visual row to (line, display column of the segment start, is phantom).</summary>
    private (int Line, int SegmentStartDisplay, bool Phantom) LocateRow(int row)
    {
        if (!UsesRowMap)
        {
            return (row, 0, false);
        }

        EnsureRowPrefix();
        int line = FindBlockOfRow(row);
        int offset = row - _rowPrefix![line];
        int ph = PhantomsBefore(line);
        if (offset < ph || line >= LineCount)
        {
            return (Math.Min(line, Math.Max(0, LineCount - 1)), 0, true);
        }

        return (line, (offset - ph) * (WordWrap ? _wrapColumns : 0), false);
    }
}
