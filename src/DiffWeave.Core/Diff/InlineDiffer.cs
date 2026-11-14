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
    /// <summary>Diffs two lines. The token sequences are compared with a minimal Myers diff.</summary>
    public static InlineDiffResult Diff(string left, string right, InlineDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= InlineDiffOptions.Default;
        if (left == right)
        {
            return InlineDiffResult.None;
        }

        IReadOnlyList<TextRange> lt = Tokenize(left, options.Granularity);
        IReadOnlyList<TextRange> rt = Tokenize(right, options.Granularity);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] li = Intern(left, lt, options, map);
        int[] ri = Intern(right, rt, options, map);
        DiffResult diff = LineDiffer.DiffIds(li, ri, new DiffOptions { Minimal = true }, CancellationToken.None);

        var lr = new List<TextRange>();
        var rr = new List<TextRange>();
        foreach (DiffHunk h in diff.Hunks)
        {
            AddTokens(lr, left, lt, h.StartA, h.CountA, options);
            AddTokens(rr, right, rt, h.StartB, h.CountB, options);
        }

        if (options.Whitespace == WhitespaceMode.IgnoreTrailing)
        {
            Trim(lr, TrailingStart(left));
            Trim(rr, TrailingStart(right));
        }

        return new InlineDiffResult(MergeRanges(lr, options.MergeGap), MergeRanges(rr, options.MergeGap));
    }

    /// <summary>Splits a line into tokens (ranges) according to <paramref name="granularity"/>.</summary>
    public static IReadOnlyList<TextRange> Tokenize(string line, InlineGranularity granularity)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<TextRange>();
        int i = 0;
        while (i < line.Length)
        {
            int start = i;
            char c = line[i];
            if (granularity == InlineGranularity.Character)
            {
                i += char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
            }
            else if (IsWordChar(c))
            {
                while (i < line.Length && IsWordChar(line[i]))
                {
                    i++;
                }
            }
            else if (char.IsWhiteSpace(c))
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
            }
            else
            {
                i += char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? 2 : 1;
            }

            tokens.Add(new TextRange(start, i - start));
        }

        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsWhitespaceToken(string line, TextRange t) => t.Length > 0 && char.IsWhiteSpace(line[t.Start]);

    private static int[] Intern(string line, IReadOnlyList<TextRange> tokens, InlineDiffOptions options, Dictionary<string, int> map)
    {
        var ids = new List<int>(tokens.Count);
        foreach (TextRange t in tokens)
        {
            string key = line.Substring(t.Start, t.Length);
            if (IsWhitespaceToken(line, t) && options.Whitespace is WhitespaceMode.IgnoreAmount or WhitespaceMode.IgnoreAll)
            {
                key = " ";
            }
            else if (options.IgnoreCase)
            {
                key = key.ToUpperInvariant();
            }

            if (!map.TryGetValue(key, out int id))
            {
                id = map.Count;
                map.Add(key, id);
            }

            ids.Add(id);
        }

        return [.. ids];
    }

    private static void AddTokens(List<TextRange> ranges, string line, IReadOnlyList<TextRange> tokens, int start, int count, InlineDiffOptions options)
    {
        for (int k = start; k < start + count; k++)
        {
            TextRange t = tokens[k];
            if (options.Whitespace == WhitespaceMode.IgnoreAll && IsWhitespaceToken(line, t))
            {
                continue;
            }

            if (ranges.Count > 0 && ranges[^1].End == t.Start)
            {
                ranges[^1] = new TextRange(ranges[^1].Start, t.End - ranges[^1].Start);
            }
            else
            {
                ranges.Add(t);
            }
        }
    }

    private static int TrailingStart(string line)
    {
        int n = line.Length;
        while (n > 0 && char.IsWhiteSpace(line[n - 1]))
        {
            n--;
        }

        return n;
    }

    private static void Trim(List<TextRange> ranges, int limit)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            TextRange r = ranges[i];
            if (r.Start >= limit)
            {
                ranges.RemoveAt(i);
            }
            else if (r.End > limit)
            {
                ranges[i] = new TextRange(r.Start, limit - r.Start);
            }
        }
    }

    private static List<TextRange> MergeRanges(List<TextRange> ranges, int gap)
    {
        if (gap <= 0 || ranges.Count < 2)
        {
            return ranges;
        }

        var result = new List<TextRange> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            TextRange last = result[^1];
            if (ranges[i].Start - last.End <= gap)
            {
                result[^1] = new TextRange(last.Start, ranges[i].End - last.Start);
            }
            else
            {
                result.Add(ranges[i]);
            }
        }

        return result;
    }
}
