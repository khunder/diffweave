namespace DiffWeave.Core.Diff;

/// <summary>
/// Side-by-side alignment of two files: pane 0 (<see cref="PaneA"/>) and pane 1 (<see cref="PaneB"/>).
/// Inside a hunk, lines are paired top-down by similarity; unpaired lines get filler rows in the other pane
/// (<see cref="RowKind.Removed"/> / <see cref="RowKind.Added"/>).
/// </summary>
public sealed class TwoWayAlignment : LineAlignment
{
    /// <summary>Index of the first pane.</summary>
    public const int PaneA = 0;

    /// <summary>Index of the second pane.</summary>
    public const int PaneB = 1;

    private TwoWayAlignment(int[][] columns, RowKind[] kinds, DiffResult diff, IReadOnlyList<string> a, IReadOnlyList<string> b)
        : base(columns, kinds, null)
    {
        Diff = diff;
        LinesA = a;
        LinesB = b;
    }

    /// <summary>The underlying line diff.</summary>
    public DiffResult Diff { get; }

    /// <summary>The lines of A.</summary>
    public IReadOnlyList<string> LinesA { get; }

    /// <summary>The lines of B.</summary>
    public IReadOnlyList<string> LinesB { get; }

    /// <summary>
    /// Diffs and aligns two files. Rows are <see cref="RowKind.WhitespaceOnly"/> when the paired lines differ only in
    /// whitespace, when an added/removed line is blank, when the lines compare equal only thanks to the whitespace/case
    /// options, and for rows of ignorable hunks.
    /// </summary>
    public static TwoWayAlignment Create(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= DiffOptions.Default;
        DiffResult diff = LineDiffer.Diff(a, b, options, cancellationToken);
        return FromDiff(a, b, diff, options);
    }

    /// <summary>Builds the alignment from an existing diff of <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static TwoWayAlignment FromDiff(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffResult diff, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        options ??= DiffOptions.Default;
        int capacity = a.Count + diff.Hunks.Sum(h => Math.Max(0, h.CountB - h.CountA));
        var colA = new List<int>(capacity);
        var colB = new List<int>(capacity);
        var kinds = new List<RowKind>(capacity);
        bool normalizes = options.NormalizesLines;

        void Equal(int i, int j)
        {
            colA.Add(i);
            colB.Add(j);
            kinds.Add(normalizes && a[i] != b[j] ? RowKind.WhitespaceOnly : RowKind.Equal);
        }

        int ia = 0, ib = 0;
        foreach (DiffHunk h in diff.Hunks)
        {
            while (ia < h.StartA)
            {
                Equal(ia++, ib++);
            }

            foreach ((int ra, int rb) in LinePairing.Pair(a, h.StartA, h.CountA, b, h.StartB, h.CountB))
            {
                colA.Add(ra);
                colB.Add(rb);
                RowKind kind;
                if (h.IsIgnorable)
                {
                    kind = RowKind.WhitespaceOnly;
                }
                else if (ra >= 0 && rb >= 0)
                {
                    kind = LineDiffer.EqualIgnoringWhitespace(a[ra], b[rb]) ? RowKind.WhitespaceOnly : RowKind.Changed;
                }
                else if (ra >= 0)
                {
                    kind = LineDiffer.IsBlank(a[ra]) ? RowKind.WhitespaceOnly : RowKind.Removed;
                }
                else
                {
                    kind = LineDiffer.IsBlank(b[rb]) ? RowKind.WhitespaceOnly : RowKind.Added;
                }

                kinds.Add(kind);
            }

            ia = h.EndA;
            ib = h.EndB;
        }

        while (ia < a.Count)
        {
            Equal(ia++, ib++);
        }

        return new TwoWayAlignment([[.. colA], [.. colB]], [.. kinds], diff, a, b);
    }
}
