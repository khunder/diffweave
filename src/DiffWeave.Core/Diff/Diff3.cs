namespace DiffWeave.Core.Diff;

/// <summary>How the left and right inputs of a three-way comparison relate to the base in one chunk.</summary>
public enum ThreeWayChange : byte
{
    /// <summary>All three agree.</summary>
    None,

    /// <summary>Only left differs from the base.</summary>
    LeftChanged,

    /// <summary>Only right differs from the base.</summary>
    RightChanged,

    /// <summary>Left and right differ from the base in the same way.</summary>
    BothChangedSame,

    /// <summary>Left and right differ from the base and from each other.</summary>
    Conflict,
}

/// <summary>A chunk of a three-way comparison.</summary>
/// <param name="Change">The relation of the inputs in this chunk.</param>
/// <param name="Base">Base lines.</param>
/// <param name="Left">Left lines.</param>
/// <param name="Right">Right lines.</param>
public readonly record struct Diff3Chunk(ThreeWayChange Change, LineRange Base, LineRange Left, LineRange Right);

/// <summary>
/// Three-way comparison built from two two-way diffs (base→left and base→right), as in GNU diff3 and git's merge:
/// overlapping or touching changes are coalesced into one chunk, which is a conflict unless both sides produced
/// identical text.
/// </summary>
public static class Diff3
{
    /// <summary>Computes the chunks covering all lines of all three inputs, in order. Adjacent chunks never are both <see cref="ThreeWayChange.None"/>.</summary>
    public static IReadOnlyList<Diff3Chunk> Compute(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseLines);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= DiffOptions.Default;

        // Blank-line hunks still change the text, so the merge must not ignore them.
        DiffOptions diffOptions = options with { IgnoreBlankLines = false };
        IReadOnlyList<DiffHunk> hl = LineDiffer.Diff(baseLines, left, diffOptions, cancellationToken).Hunks;
        IReadOnlyList<DiffHunk> hr = LineDiffer.Diff(baseLines, right, diffOptions, cancellationToken).Hunks;
        return Combine(baseLines, left, right, hl, hr, options);
    }

    /// <summary>Combines the base→left and base→right hunks into chunks.</summary>
    internal static List<Diff3Chunk> Combine(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, IReadOnlyList<DiffHunk> hl, IReadOnlyList<DiffHunk> hr, DiffOptions options)
    {
        var chunks = new List<Diff3Chunk>();
        int il = 0, ir = 0;
        int offL = 0, offR = 0; // (left/right line) - (base line) outside of hunks processed so far
        int basePos = 0;
        while (il < hl.Count || ir < hr.Count)
        {
            int start;
            if (ir >= hr.Count || (il < hl.Count && hl[il].StartA <= hr[ir].StartA))
            {
                start = hl[il].StartA;
            }
            else
            {
                start = hr[ir].StartA;
            }

            int end = start;
            int newOffL = offL, newOffR = offR;
            bool anyL = false, anyR = false;
            bool grew = true;
            while (grew)
            {
                grew = false;
                while (il < hl.Count && hl[il].StartA <= end && (hl[il].StartA >= start))
                {
                    end = Math.Max(end, hl[il].EndA);
                    newOffL += hl[il].CountB - hl[il].CountA;
                    anyL = true;
                    il++;
                    grew = true;
                }

                while (ir < hr.Count && hr[ir].StartA <= end && (hr[ir].StartA >= start))
                {
                    end = Math.Max(end, hr[ir].EndA);
                    newOffR += hr[ir].CountB - hr[ir].CountA;
                    anyR = true;
                    ir++;
                    grew = true;
                }
            }

            if (start > basePos)
            {
                chunks.Add(new Diff3Chunk(ThreeWayChange.None, new LineRange(basePos, start - basePos),
                    new LineRange(basePos + offL, start - basePos), new LineRange(basePos + offR, start - basePos)));
            }

            var baseRange = new LineRange(start, end - start);
            var leftRange = new LineRange(start + offL, end - start + newOffL - offL);
            var rightRange = new LineRange(start + offR, end - start + newOffR - offR);
            ThreeWayChange change;
            if (anyL && !anyR)
            {
                change = ThreeWayChange.LeftChanged;
            }
            else if (anyR && !anyL)
            {
                change = ThreeWayChange.RightChanged;
            }
            else
            {
                change = RangesEqual(left, leftRange, right, rightRange, options) ? ThreeWayChange.BothChangedSame : ThreeWayChange.Conflict;
            }

            chunks.Add(new Diff3Chunk(change, baseRange, leftRange, rightRange));
            offL = newOffL;
            offR = newOffR;
            basePos = end;
        }

        if (basePos < baseLines.Count || chunks.Count == 0)
        {
            int n = baseLines.Count - basePos;
            if (n > 0 || chunks.Count == 0)
            {
                chunks.Add(new Diff3Chunk(ThreeWayChange.None, new LineRange(basePos, n), new LineRange(basePos + offL, n), new LineRange(basePos + offR, n)));
            }
        }

        // An empty "None" chunk (all three inputs empty) is dropped.
        chunks.RemoveAll(c => c.Change == ThreeWayChange.None && c.Base.Count == 0 && c.Left.Count == 0 && c.Right.Count == 0);
        return chunks;
    }

    /// <summary>Whether two line ranges are equal under the options.</summary>
    internal static bool RangesEqual(IReadOnlyList<string> a, LineRange ra, IReadOnlyList<string> b, LineRange rb, DiffOptions options)
    {
        if (ra.Count != rb.Count)
        {
            return false;
        }

        for (int k = 0; k < ra.Count; k++)
        {
            if (!LineDiffer.LinesEqual(a[ra.Start + k], b[rb.Start + k], options))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether two line ranges are equal when all whitespace (including blank lines) is ignored.</summary>
    internal static bool RangesEqualIgnoringWhitespace(IReadOnlyList<string> a, LineRange ra, IReadOnlyList<string> b, LineRange rb)
    {
        var sa = new System.Text.StringBuilder();
        var sb = new System.Text.StringBuilder();
        for (int k = ra.Start; k < ra.End; k++)
        {
            foreach (char c in a[k])
            {
                if (!char.IsWhiteSpace(c))
                {
                    sa.Append(c);
                }
            }
        }

        for (int k = rb.Start; k < rb.End; k++)
        {
            foreach (char c in b[k])
            {
                if (!char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                }
            }
        }

        return sa.Equals(sb);
    }
}

/// <summary>
/// Side-by-side alignment of three files: pane 0 = base (A), pane 1 = left (B), pane 2 = right (C).
/// Unchanged chunks are aligned line by line; inside changed chunks, the lines are aligned with the help of the
/// pairwise diffs and padded with filler rows.
/// </summary>
public sealed class ThreeWayAlignment : LineAlignment
{
    /// <summary>Index of the base pane.</summary>
    public const int BasePane = 0;

    /// <summary>Index of the left pane.</summary>
    public const int LeftPane = 1;

    /// <summary>Index of the right pane.</summary>
    public const int RightPane = 2;

    private readonly ThreeWayChange[] _changes;
    private readonly int[] _chunkOfRow;

    private ThreeWayAlignment(int[][] columns, RowKind[] kinds, ThreeWayChange[] changes, int[] chunkOfRow, IReadOnlyList<Diff3Chunk> chunks)
        : base(columns, kinds, chunkOfRow)
    {
        _changes = changes;
        _chunkOfRow = chunkOfRow;
        Chunks = chunks;
    }

    /// <summary>The underlying chunks.</summary>
    public IReadOnlyList<Diff3Chunk> Chunks { get; }

    /// <summary>The three-way relation of the chunk a row belongs to.</summary>
    public ThreeWayChange GetChange(int row) => _changes[row];

    /// <summary>The index in <see cref="Chunks"/> of the chunk a row belongs to.</summary>
    public int GetChunkIndex(int row) => _chunkOfRow[row];

    /// <summary>
    /// Compares and aligns three files. Row kinds: <see cref="RowKind.Equal"/>, <see cref="RowKind.Changed"/> (one side
    /// or both sides identically), <see cref="RowKind.Conflict"/>, <see cref="RowKind.WhitespaceOnly"/> (non-equal rows
    /// whose present lines are all equal when ignoring whitespace).
    /// </summary>
    public static ThreeWayAlignment Create(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DiffOptions.Default;
        IReadOnlyList<Diff3Chunk> chunks = Diff3.Compute(baseLines, left, right, options, cancellationToken);
        return FromChunks(baseLines, left, right, chunks, options);
    }

    /// <summary>Builds the alignment from precomputed chunks.</summary>
    public static ThreeWayAlignment FromChunks(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, IReadOnlyList<Diff3Chunk> chunks, DiffOptions? options = null)
    {
        options ??= DiffOptions.Default;
        var cb = new List<int>();
        var cl = new List<int>();
        var cr = new List<int>();
        var kinds = new List<RowKind>();
        var changes = new List<ThreeWayChange>();
        var chunkOfRow = new List<int>();
        for (int c = 0; c < chunks.Count; c++)
        {
            Diff3Chunk chunk = chunks[c];
            foreach ((int b, int l, int r) in LayoutChunk(baseLines, left, right, chunk))
            {
                cb.Add(b);
                cl.Add(l);
                cr.Add(r);
                kinds.Add(ClassifyRow(baseLines, left, right, chunk, b, l, r, options));
                changes.Add(chunk.Change);
                chunkOfRow.Add(c);
            }
        }

        return new ThreeWayAlignment([[.. cb], [.. cl], [.. cr]], [.. kinds], [.. changes], [.. chunkOfRow], chunks);
    }

    /// <summary>The row kind of one row of a chunk.</summary>
    internal static RowKind ClassifyRow(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, Diff3Chunk chunk, int b, int l, int r, DiffOptions options)
    {
        if (chunk.Change == ThreeWayChange.None)
        {
            bool same = baseLines[b] == left[l] && baseLines[b] == right[r];
            return same ? RowKind.Equal : RowKind.WhitespaceOnly;
        }

        if (chunk.Change == ThreeWayChange.Conflict)
        {
            return Diff3.RangesEqualIgnoringWhitespace(left, chunk.Left, right, chunk.Right) ? RowKind.WhitespaceOnly : RowKind.Conflict;
        }

        string? first = null;
        bool allEqualWs = true, anyFiller = false, allBlank = true;
        foreach ((IReadOnlyList<string> lines, int idx) in new[] { (baseLines, b), (left, l), (right, r) })
        {
            if (idx < 0)
            {
                anyFiller = true;
                continue;
            }

            string s = lines[idx];
            allBlank &= LineDiffer.IsBlank(s);
            if (first is null)
            {
                first = s;
            }
            else if (!LineDiffer.EqualIgnoringWhitespace(first, s))
            {
                allEqualWs = false;
            }
        }

        if (anyFiller ? allBlank : allEqualWs)
        {
            return RowKind.WhitespaceOnly;
        }

        return RowKind.Changed;
    }

    /// <summary>
    /// Lays out the rows (base, left, right line or -1) of one chunk. Sides that are identical to another side share
    /// its rows; the changed side is paired against them by similarity.
    /// </summary>
    internal static List<(int B, int L, int R)> LayoutChunk(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, Diff3Chunk chunk)
    {
        var rows = new List<(int, int, int)>();
        switch (chunk.Change)
        {
            case ThreeWayChange.None:
                for (int k = 0; k < chunk.Base.Count; k++)
                {
                    rows.Add((chunk.Base.Start + k, chunk.Left.Start + k, chunk.Right.Start + k));
                }

                break;

            case ThreeWayChange.LeftChanged when chunk.Base.Count == chunk.Right.Count:
                foreach ((int b, int l) in LinePairing.Pair(baseLines, chunk.Base.Start, chunk.Base.Count, left, chunk.Left.Start, chunk.Left.Count))
                {
                    rows.Add((b, l, b < 0 ? -1 : chunk.Right.Start + (b - chunk.Base.Start)));
                }

                break;

            case ThreeWayChange.RightChanged when chunk.Base.Count == chunk.Left.Count:
                foreach ((int b, int r) in LinePairing.Pair(baseLines, chunk.Base.Start, chunk.Base.Count, right, chunk.Right.Start, chunk.Right.Count))
                {
                    rows.Add((b, b < 0 ? -1 : chunk.Left.Start + (b - chunk.Base.Start), r));
                }

                break;

            case ThreeWayChange.BothChangedSame when chunk.Left.Count == chunk.Right.Count:
                foreach ((int b, int l) in LinePairing.Pair(baseLines, chunk.Base.Start, chunk.Base.Count, left, chunk.Left.Start, chunk.Left.Count))
                {
                    rows.Add((b, l, l < 0 ? -1 : chunk.Right.Start + (l - chunk.Left.Start)));
                }

                break;

            default:
            {
                // Conflict (or unequal counts): pair left with right, then put base lines next to their best partner rows top-down.
                List<(int L, int R)> lr = LinePairing.Pair(left, chunk.Left.Start, chunk.Left.Count, right, chunk.Right.Start, chunk.Right.Count);
                int n = Math.Max(lr.Count, chunk.Base.Count);
                for (int k = 0; k < n; k++)
                {
                    int b = k < chunk.Base.Count ? chunk.Base.Start + k : -1;
                    (int l, int r) = k < lr.Count ? lr[k] : (-1, -1);
                    rows.Add((b, l, r));
                }

                break;
            }
        }

        return rows;
    }
}
