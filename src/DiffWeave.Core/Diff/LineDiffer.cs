namespace DiffWeave.Core.Diff;

/// <summary>Computes line-level differences between two sequences of lines.</summary>
public static class LineDiffer
{
    /// <summary>
    /// Diffs <paramref name="a"/> against <paramref name="b"/>. Thread-safe; the inputs are not modified and must
    /// not change during the call.
    /// </summary>
    public static DiffResult Diff(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>Returns the comparison key of a line under the given options (the line itself when nothing is ignored).</summary>
    public static string Normalize(string line, DiffOptions options) => throw new NotImplementedException();

    /// <summary>Whether two lines compare equal under the given options.</summary>
    public static bool LinesEqual(string a, string b, DiffOptions options) => Normalize(a, options) == Normalize(b, options);

    /// <summary>Whether two lines are equal when all whitespace is ignored (used to flag whitespace-only changes).</summary>
    public static bool EqualIgnoringWhitespace(string a, string b) => throw new NotImplementedException();

    /// <summary>Whether a line consists only of whitespace.</summary>
    public static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);
}
