namespace DiffWeave.Core.Diff;

/// <summary>A 0-based range of lines.</summary>
/// <param name="Start">First line.</param>
/// <param name="Count">Number of lines. An empty range denotes the position before line <paramref name="Start"/>.</param>
public readonly record struct LineRange(int Start, int Count)
{
    /// <summary>One past the last line.</summary>
    public int End => Start + Count;

    /// <summary>Whether the range is empty.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Whether <paramref name="line"/> lies inside the range.</summary>
    public bool Contains(int line) => line >= Start && line < End;

    /// <inheritdoc/>
    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// One difference between sequences A and B: lines <c>[StartA, StartA+CountA)</c> of A are replaced by lines
/// <c>[StartB, StartB+CountB)</c> of B. A pure insertion has <c>CountA == 0</c>, a pure deletion <c>CountB == 0</c>.
/// </summary>
/// <param name="StartA">First line in A (for insertions: the line before which B's lines are inserted).</param>
/// <param name="CountA">Number of lines removed from A.</param>
/// <param name="StartB">First line in B (for deletions: the position in B).</param>
/// <param name="CountB">Number of lines inserted from B.</param>
/// <param name="IsIgnorable">The hunk only adds/removes blank lines and <see cref="DiffOptions.IgnoreBlankLines"/> is on.</param>
public readonly record struct DiffHunk(int StartA, int CountA, int StartB, int CountB, bool IsIgnorable = false)
{
    /// <summary>The A side as a range.</summary>
    public LineRange RangeA => new(StartA, CountA);

    /// <summary>The B side as a range.</summary>
    public LineRange RangeB => new(StartB, CountB);

    /// <summary>One past the last line of A.</summary>
    public int EndA => StartA + CountA;

    /// <summary>One past the last line of B.</summary>
    public int EndB => StartB + CountB;
}

/// <summary>The result of <see cref="LineDiffer.Diff"/>.</summary>
public sealed class DiffResult
{
    /// <summary>Creates a result.</summary>
    public DiffResult(IReadOnlyList<DiffHunk> hunks, int lineCountA, int lineCountB)
    {
        Hunks = hunks;
        LineCountA = lineCountA;
        LineCountB = lineCountB;
    }

    /// <summary>Hunks ordered by position; never adjacent or overlapping (separated by at least one common line).</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; }

    /// <summary>Number of lines in A.</summary>
    public int LineCountA { get; }

    /// <summary>Number of lines in B.</summary>
    public int LineCountB { get; }

    /// <summary>Whether there is no non-ignorable hunk.</summary>
    public bool AreEqual => Hunks.All(h => h.IsIgnorable);

    /// <summary>Total of removed plus inserted lines (the edit distance of the script).</summary>
    public int EditCount => Hunks.Sum(h => h.CountA + h.CountB);
}
