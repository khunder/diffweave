namespace DiffWeave.Core.Text;

/// <summary>A line terminator.</summary>
public enum LineEnding
{
    /// <summary>No terminator (only possible for the last line of a document).</summary>
    None,

    /// <summary>"\n" (Unix).</summary>
    LF,

    /// <summary>"\r\n" (Windows).</summary>
    CRLF,

    /// <summary>"\r" (classic Mac).</summary>
    CR,
}

/// <summary>Helpers for <see cref="LineEnding"/>.</summary>
public static class LineEndings
{
    /// <summary>The terminator text ("" for <see cref="LineEnding.None"/>).</summary>
    public static string ToText(this LineEnding ending) => ending switch
    {
        LineEnding.LF => "\n",
        LineEnding.CRLF => "\r\n",
        LineEnding.CR => "\r",
        _ => string.Empty,
    };

    /// <summary>The platform default (CRLF on Windows, LF elsewhere).</summary>
    public static LineEnding PlatformDefault => OperatingSystem.IsWindows() ? LineEnding.CRLF : LineEnding.LF;

    /// <summary>
    /// Splits text into lines without terminators. A trailing terminator does not start a new (empty) line,
    /// so "a\n" yields ["a"] and "" yields []. The terminator of each line is reported in <paramref name="endings"/>.
    /// </summary>
    public static List<string> Split(string text, List<LineEnding>? endings = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        int start = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(text.Substring(start, i - start));
                LineEnding e;
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    e = LineEnding.CRLF;
                    i += 2;
                }
                else
                {
                    e = c == '\n' ? LineEnding.LF : LineEnding.CR;
                    i++;
                }

                endings?.Add(e);
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text.Substring(start));
            endings?.Add(LineEnding.None);
        }

        return lines;
    }
}
