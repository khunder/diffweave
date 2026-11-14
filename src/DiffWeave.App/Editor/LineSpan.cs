namespace DiffWeave.App.Editor;

/// <summary>A range of whole lines [Start, Start+Count). Count may be 0 (an insertion point).</summary>
public readonly record struct LineSpan(int Start, int Count)
{
    /// <summary>Exclusive end line.</summary>
    public int End => Start + Count;

    /// <summary>True when <paramref name="line"/> lies inside the span.</summary>
    public bool Contains(int line) => line >= Start && line < End;
}

/// <summary>Options for searching text in an editor.</summary>
public sealed record FindOptions(string Pattern, bool MatchCase = false, bool UseRegex = false, bool WholeWord = false)
{
    /// <summary>Builds a regex for the options; returns null for an empty or invalid pattern.</summary>
    public System.Text.RegularExpressions.Regex? TryBuildRegex()
    {
        if (string.IsNullOrEmpty(Pattern))
        {
            return null;
        }

        string p = UseRegex ? Pattern : System.Text.RegularExpressions.Regex.Escape(Pattern);
        if (WholeWord)
        {
            p = $@"\b(?:{p})\b";
        }

        System.Text.RegularExpressions.RegexOptions o = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        if (!MatchCase)
        {
            o |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        }

        try
        {
            return new System.Text.RegularExpressions.Regex(p, o, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
