namespace DiffWeave.Core.Diff;

/// <summary>The line-diff algorithm.</summary>
public enum DiffAlgorithm
{
    /// <summary>Myers' O(ND) algorithm (linear-space divide and conquer); minimal edit script.</summary>
    Myers,

    /// <summary>Patience diff: anchors on lines unique in both files, Myers between anchors. Often more readable for code.</summary>
    Patience,

    /// <summary>Histogram diff (as in git): anchors on the least frequent common lines, Myers as fallback.</summary>
    Histogram,
}

/// <summary>How whitespace is treated when comparing lines.</summary>
public enum WhitespaceMode
{
    /// <summary>Whitespace is significant.</summary>
    None,

    /// <summary>Whitespace at the end of lines is ignored.</summary>
    IgnoreTrailing,

    /// <summary>Changes in the amount of whitespace are ignored (runs compare equal, trailing ignored; like <c>git diff -b</c>).</summary>
    IgnoreAmount,

    /// <summary>All whitespace is ignored (like <c>git diff -w</c>).</summary>
    IgnoreAll,
}

/// <summary>Options for <see cref="LineDiffer"/>.</summary>
public sealed record DiffOptions
{
    /// <summary>Default options: Myers, everything significant.</summary>
    public static DiffOptions Default { get; } = new();

    /// <summary>The algorithm.</summary>
    public DiffAlgorithm Algorithm { get; init; } = DiffAlgorithm.Myers;

    /// <summary>Whitespace handling.</summary>
    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.None;

    /// <summary>Compare lines case-insensitively (ordinal, invariant upper-casing).</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>
    /// Hunks that only insert or delete blank lines (after whitespace normalization) are flagged
    /// <see cref="DiffHunk.IsIgnorable"/> (like GNU <c>diff -B</c>).
    /// </summary>
    public bool IgnoreBlankLines { get; init; }

    /// <summary>
    /// When <see langword="false"/> (default) the Myers search gives up on very expensive edit paths and uses a
    /// near-minimal split instead (like git's xdiff heuristics), which bounds run time on huge, very different files.
    /// When <see langword="true"/> the result is always a minimal edit script.
    /// </summary>
    public bool Minimal { get; init; }

    /// <summary>Whether any option changes how lines compare (i.e. lines may compare equal while differing textually).</summary>
    public bool NormalizesLines => Whitespace != WhitespaceMode.None || IgnoreCase;
}
