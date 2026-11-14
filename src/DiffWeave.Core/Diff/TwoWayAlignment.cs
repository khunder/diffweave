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
        => throw new NotImplementedException();
}
