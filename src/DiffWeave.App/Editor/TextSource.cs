namespace DiffWeave.App.Editor;

/// <summary>A position in a line-based document (zero-based line and column, column counted in UTF-16 chars).</summary>
public readonly record struct TextPos(int Line, int Column) : IComparable<TextPos>
{
    /// <inheritdoc />
    public int CompareTo(TextPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    /// <summary>Returns true when <paramref name="a"/> is before <paramref name="b"/>.</summary>
    public static bool operator <(TextPos a, TextPos b) => a.CompareTo(b) < 0;

    /// <summary>Returns true when <paramref name="a"/> is after <paramref name="b"/>.</summary>
    public static bool operator >(TextPos a, TextPos b) => a.CompareTo(b) > 0;

    /// <summary>Returns true when <paramref name="a"/> is before or equal to <paramref name="b"/>.</summary>
    public static bool operator <=(TextPos a, TextPos b) => a.CompareTo(b) <= 0;

    /// <summary>Returns true when <paramref name="a"/> is after or equal to <paramref name="b"/>.</summary>
    public static bool operator >=(TextPos a, TextPos b) => a.CompareTo(b) >= 0;
}

/// <summary>A half-open text range [Start, End) with Start &lt;= End.</summary>
public readonly record struct TextRange(TextPos Start, TextPos End)
{
    /// <summary>Creates a normalized range from two positions in any order.</summary>
    public static TextRange FromPoints(TextPos a, TextPos b) => a <= b ? new TextRange(a, b) : new TextRange(b, a);

    /// <summary>True when the range is empty.</summary>
    public bool IsEmpty => Start == End;
}

/// <summary>Describes a change of a text source: lines [StartLine, StartLine+RemovedLines) were replaced by InsertedLines lines.</summary>
public sealed class TextSourceChangedEventArgs(int startLine, int removedLines, int insertedLines) : EventArgs
{
    /// <summary>A change that invalidates the whole document.</summary>
    public static TextSourceChangedEventArgs Reset { get; } = new(0, -1, -1);

    /// <summary>First affected line.</summary>
    public int StartLine { get; } = startLine;

    /// <summary>Number of removed lines (-1 for a reset).</summary>
    public int RemovedLines { get; } = removedLines;

    /// <summary>Number of inserted lines (-1 for a reset).</summary>
    public int InsertedLines { get; } = insertedLines;

    /// <summary>True when the whole document changed.</summary>
    public bool IsReset => RemovedLines < 0;
}

/// <summary>
/// The text model displayed by <see cref="Controls.DiffTextView"/>. Lines never contain line terminators.
/// Editable sources apply edits themselves (for the merge output this goes through the merge session) and raise <see cref="Changed"/>.
/// </summary>
public interface IEditorTextSource
{
    /// <summary>Number of lines (at least 0; an empty document has 0 or 1 empty line).</summary>
    int LineCount { get; }

    /// <summary>Returns the text of a line without terminator.</summary>
    string GetLine(int line);

    /// <summary>True when <see cref="Replace"/> is supported.</summary>
    bool IsEditable { get; }

    /// <summary>Replaces <paramref name="range"/> with <paramref name="text"/> ('\n' separates lines) and returns the position after the inserted text.</summary>
    TextPos Replace(TextRange range, string text);

    /// <summary>True when an undo step is available.</summary>
    bool CanUndo { get; }

    /// <summary>True when a redo step is available.</summary>
    bool CanRedo { get; }

    /// <summary>Undoes the last edit; returns the caret position to restore or null.</summary>
    TextPos? Undo();

    /// <summary>Redoes the last undone edit; returns the caret position to restore or null.</summary>
    TextPos? Redo();

    /// <summary>Raised after the text changed.</summary>
    event EventHandler<TextSourceChangedEventArgs>? Changed;
}

/// <summary>Helpers shared by text source implementations.</summary>
public static class TextSourceExtensions
{
    /// <summary>Returns the text in <paramref name="range"/> using '\n' between lines.</summary>
    public static string GetText(this IEditorTextSource source, TextRange range)
    {
        if (source.LineCount == 0 || range.IsEmpty)
        {
            return string.Empty;
        }

        TextPos s = source.Clamp(range.Start);
        TextPos e = source.Clamp(range.End);
        if (s.Line == e.Line)
        {
            return source.GetLine(s.Line)[s.Column..e.Column];
        }

        System.Text.StringBuilder sb = new();
        sb.Append(source.GetLine(s.Line).AsSpan(s.Column));
        for (int i = s.Line + 1; i < e.Line; i++)
        {
            sb.Append('\n').Append(source.GetLine(i));
        }

        sb.Append('\n').Append(source.GetLine(e.Line).AsSpan(0, e.Column));
        return sb.ToString();
    }

    /// <summary>Clamps a position into the document.</summary>
    public static TextPos Clamp(this IEditorTextSource source, TextPos pos)
    {
        if (source.LineCount == 0)
        {
            return default;
        }

        int line = Math.Clamp(pos.Line, 0, source.LineCount - 1);
        int col = Math.Clamp(pos.Column, 0, source.GetLine(line).Length);
        return new TextPos(line, col);
    }

    /// <summary>The position after the last character.</summary>
    public static TextPos EndPosition(this IEditorTextSource source)
        => source.LineCount == 0 ? default : new TextPos(source.LineCount - 1, source.GetLine(source.LineCount - 1).Length);

    /// <summary>Splits text on \r\n, \r or \n.</summary>
    public static string[] SplitLines(string text) => text.ReplaceLineEndings("\n").Split('\n');
}

/// <summary>A read-only text source over a list of lines.</summary>
public sealed class ReadOnlyTextSource(IReadOnlyList<string> lines) : IEditorTextSource
{
    private IReadOnlyList<string> _lines = lines;

    /// <summary>An empty source.</summary>
    public static ReadOnlyTextSource Empty => new([]);

    /// <inheritdoc />
    public int LineCount => _lines.Count;

    /// <inheritdoc />
    public bool IsEditable => false;

    /// <inheritdoc />
    public bool CanUndo => false;

    /// <inheritdoc />
    public bool CanRedo => false;

    /// <inheritdoc />
    public event EventHandler<TextSourceChangedEventArgs>? Changed;

    /// <inheritdoc />
    public string GetLine(int line) => _lines[line];

    /// <summary>Replaces all lines.</summary>
    public void SetLines(IReadOnlyList<string> lines)
    {
        _lines = lines;
        Changed?.Invoke(this, TextSourceChangedEventArgs.Reset);
    }

    /// <inheritdoc />
    public TextPos Replace(TextRange range, string text) => throw new NotSupportedException("The document is read-only.");

    /// <inheritdoc />
    public TextPos? Undo() => null;

    /// <inheritdoc />
    public TextPos? Redo() => null;
}

/// <summary>An in-memory editable text source with its own undo stack (used for free editing and tests).</summary>
public sealed class EditableTextSource : IEditorTextSource
{
    private readonly List<string> _lines;
    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();

    private sealed record Edit(TextRange RangeBefore, string TextBefore, TextRange RangeAfter, string TextAfter);

    /// <summary>Creates a source from text.</summary>
    public EditableTextSource(string text = "")
    {
        _lines = [.. TextSourceExtensions.SplitLines(text)];
    }

    /// <inheritdoc />
    public int LineCount => _lines.Count;

    /// <inheritdoc />
    public bool IsEditable => true;

    /// <inheritdoc />
    public bool CanUndo => _undo.Count > 0;

    /// <inheritdoc />
    public bool CanRedo => _redo.Count > 0;

    /// <inheritdoc />
    public event EventHandler<TextSourceChangedEventArgs>? Changed;

    /// <summary>The whole text with '\n' separators.</summary>
    public string Text => string.Join('\n', _lines);

    /// <inheritdoc />
    public string GetLine(int line) => _lines[line];

    /// <inheritdoc />
    public TextPos Replace(TextRange range, string text)
    {
        string before = this.GetText(range);
        TextPos end = ReplaceCore(range, text);
        _undo.Push(new Edit(range, before, new TextRange(range.Start, end), text));
        _redo.Clear();
        return end;
    }

    /// <inheritdoc />
    public TextPos? Undo()
    {
        if (!_undo.TryPop(out Edit? e))
        {
            return null;
        }

        TextPos end = ReplaceCore(e.RangeAfter, e.TextBefore);
        _redo.Push(e);
        return end;
    }

    /// <inheritdoc />
    public TextPos? Redo()
    {
        if (!_redo.TryPop(out Edit? e))
        {
            return null;
        }

        TextPos end = ReplaceCore(e.RangeBefore, e.TextAfter);
        _undo.Push(e);
        return end;
    }

    private TextPos ReplaceCore(TextRange range, string text)
    {
        TextPos s = this.Clamp(range.Start);
        TextPos e = this.Clamp(range.End);
        string prefix = _lines[s.Line][..s.Column];
        string suffix = _lines[e.Line][e.Column..];
        string[] parts = TextSourceExtensions.SplitLines(text);
        parts[0] = prefix + parts[0];
        TextPos endPos = new(s.Line + parts.Length - 1, parts[^1].Length);
        parts[^1] += suffix;
        int removed = e.Line - s.Line + 1;
        _lines.RemoveRange(s.Line, removed);
        _lines.InsertRange(s.Line, parts);
        Changed?.Invoke(this, new TextSourceChangedEventArgs(s.Line, removed, parts.Length));
        return endPos;
    }
}
