namespace DiffWeave.Core.Diff;

/// <summary>The unit of an inline (intra-line) diff.</summary>
public enum InlineGranularity
{
    /// <summary>Words (runs of letters, digits and '_'), whitespace runs and single punctuation characters.</summary>
    Word,

    /// <summary>Single characters (surrogate pairs are kept together).</summary>
    Character,
}

/// <summary>Options for <see cref="InlineDiffer"/>.</summary>
public sealed record InlineDiffOptions
{
    /// <summary>Default options: word granularity, everything significant.</summary>
    public static InlineDiffOptions Default { get; } = new();

    /// <summary>The token granularity.</summary>
    public InlineGranularity Granularity { get; init; } = InlineGranularity.Word;

    /// <summary>
    /// Whitespace handling: with <see cref="WhitespaceMode.IgnoreAll"/> whitespace tokens never count as changes;
    /// with <see cref="WhitespaceMode.IgnoreAmount"/> any whitespace run matches any other run;
    /// with <see cref="WhitespaceMode.IgnoreTrailing"/> trailing whitespace never counts as a change.
    /// </summary>
    public WhitespaceMode Whitespace { get; init; } = WhitespaceMode.None;

    /// <summary>Compare tokens case-insensitively.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>
    /// Changed ranges separated by at most this many unchanged characters are merged into one range, which makes
    /// highlighting less noisy (KDiff3 behaves similarly). 0 disables merging.
    /// </summary>
    public int MergeGap { get; init; }
}

/// <summary>A range of characters in a line.</summary>
/// <param name="Start">First character (UTF-16 index).</param>
/// <param name="Length">Number of characters.</param>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>One past the last character.</summary>
    public int End => Start + Length;
}

/// <summary>The changed character ranges of two lines.</summary>
/// <param name="Left">Ranges in the left line that are not in the right line (sorted, non-overlapping).</param>
/// <param name="Right">Ranges in the right line that are not in the left line (sorted, non-overlapping).</param>
public sealed record InlineDiffResult(IReadOnlyList<TextRange> Left, IReadOnlyList<TextRange> Right)
{
    /// <summary>A result without changes.</summary>
    public static InlineDiffResult None { get; } = new([], []);

    /// <summary>Whether no ranges are reported.</summary>
    public bool IsEmpty => Left.Count == 0 && Right.Count == 0;
}

/// <summary>Word- or character-level diff of a pair of lines (for highlighting inside changed lines).</summary>
public static class InlineDiffer
{
    /// <summary>Diffs two lines.</summary>
    public static InlineDiffResult Diff(string left, string right, InlineDiffOptions? options = null)
        => throw new NotImplementedException();

    /// <summary>Splits a line into tokens (ranges) according to <paramref name="granularity"/>.</summary>
    public static IReadOnlyList<TextRange> Tokenize(string line, InlineGranularity granularity)
        => throw new NotImplementedException();
}
