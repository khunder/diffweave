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
        => throw new NotImplementedException();
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
        => throw new NotImplementedException();
}
