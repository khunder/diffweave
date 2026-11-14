namespace DiffWeave.App.Editor;

/// <summary>
/// Semantic highlight categories. The editor maps each kind to theme brushes
/// (<c>DW.Kind.{Kind}.Line</c>, <c>DW.Kind.{Kind}.Inline</c>, <c>DW.Kind.{Kind}.Mark</c>), never to hard-coded colours.
/// </summary>
public enum HighlightKind
{
    /// <summary>No highlight.</summary>
    None,

    /// <summary>Lines present only in this side.</summary>
    Inserted,

    /// <summary>Lines removed relative to the other side.</summary>
    Deleted,

    /// <summary>Lines modified relative to the other side.</summary>
    Changed,

    /// <summary>Change contributed by input B.</summary>
    SourceB,

    /// <summary>Change contributed by input C.</summary>
    SourceC,

    /// <summary>Change taken from input A (base).</summary>
    SourceA,

    /// <summary>An unresolved conflict.</summary>
    Conflict,

    /// <summary>A conflict that has been resolved.</summary>
    Resolved,

    /// <summary>Informational decoration (plugins).</summary>
    Info,

    /// <summary>Warning decoration (plugins).</summary>
    Warning,

    /// <summary>Error decoration (plugins).</summary>
    Error,

    /// <summary>A find match.</summary>
    FindMatch,

    /// <summary>A preview of a proposed change.</summary>
    Proposal,
}

/// <summary>How an inline decoration is painted.</summary>
public enum InlineStyle
{
    /// <summary>Filled background behind the characters.</summary>
    Fill,

    /// <summary>Underline (squiggle-free straight line).</summary>
    Underline,

    /// <summary>Outline box around the characters.</summary>
    Box,
}

/// <summary>Whole-line background decoration.</summary>
public readonly record struct LineDecoration(int Line, HighlightKind Kind);

/// <summary>Character-range decoration within one line.</summary>
public readonly record struct InlineDecoration(int Line, int StartColumn, int Length, HighlightKind Kind, InlineStyle Style = InlineStyle.Fill);

/// <summary>A marker in the gutter (decoration mark) with an optional tooltip.</summary>
public readonly record struct GutterMark(int Line, HighlightKind Kind, string? Tooltip = null);

/// <summary>A thin horizontal marker drawn above <see cref="BeforeLine"/>: shows where lines exist only in the other pane.</summary>
public readonly record struct GapMarker(int BeforeLine, HighlightKind Kind);

/// <summary>Collects decorations for a visible line range; one list per render layer.</summary>
public sealed class DecorationSink
{
    /// <summary>Line backgrounds (background layer).</summary>
    public List<LineDecoration> Lines { get; } = [];

    /// <summary>Inline ranges (inline highlight layer).</summary>
    public List<InlineDecoration> Inlines { get; } = [];

    /// <summary>Gutter marks (gutter layer).</summary>
    public List<GutterMark> Marks { get; } = [];

    /// <summary>Gap markers (background layer).</summary>
    public List<GapMarker> Gaps { get; } = [];

    /// <summary>Clears all lists.</summary>
    public void Clear()
    {
        Lines.Clear();
        Inlines.Clear();
        Marks.Clear();
        Gaps.Clear();
    }
}

/// <summary>
/// Supplies decorations to a <see cref="Controls.DiffTextView"/>. The diff model is one source; editor
/// decoration plugins are adapted into further sources. Sources are queried only for the visible line range.
/// </summary>
public interface IEditorDecorationSource
{
    /// <summary>Adds the decorations for lines [firstLine, lastLine] to <paramref name="sink"/>.</summary>
    void GetDecorations(int firstLine, int lastLine, DecorationSink sink);

    /// <summary>Raised when previously returned decorations became stale.</summary>
    event EventHandler? Invalidated;
}

/// <summary>A decoration source backed by in-memory lists, indexed by line for fast range queries.</summary>
public sealed class StaticDecorationSource : IEditorDecorationSource
{
    private readonly SortedList<int, List<object>> _byLine = [];

    /// <inheritdoc />
    public event EventHandler? Invalidated;

    /// <summary>Removes everything.</summary>
    public void Clear()
    {
        _byLine.Clear();
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a line decoration.</summary>
    public void Add(LineDecoration d) => AddCore(d.Line, d);

    /// <summary>Adds an inline decoration.</summary>
    public void Add(InlineDecoration d) => AddCore(d.Line, d);

    /// <summary>Adds a gutter mark.</summary>
    public void Add(GutterMark d) => AddCore(d.Line, d);

    /// <summary>Adds a gap marker.</summary>
    public void Add(GapMarker d) => AddCore(d.BeforeLine, d);

    /// <summary>Signals that decorations changed after a batch of adds.</summary>
    public void NotifyChanged() => Invalidated?.Invoke(this, EventArgs.Empty);

    private void AddCore(int line, object d)
    {
        if (!_byLine.TryGetValue(line, out List<object>? list))
        {
            list = [];
            _byLine.Add(line, list);
        }

        list.Add(d);
    }

    /// <inheritdoc />
    public void GetDecorations(int firstLine, int lastLine, DecorationSink sink)
    {
        IList<int> keys = _byLine.Keys;
        int lo = 0, hi = keys.Count - 1, start = keys.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (keys[mid] >= firstLine)
            {
                start = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        for (int i = start; i < keys.Count && keys[i] <= lastLine; i++)
        {
            foreach (object o in _byLine.Values[i])
            {
                switch (o)
                {
                    case LineDecoration l: sink.Lines.Add(l); break;
                    case InlineDecoration n: sink.Inlines.Add(n); break;
                    case GutterMark m: sink.Marks.Add(m); break;
                    case GapMarker g: sink.Gaps.Add(g); break;
                }
            }
        }
    }
}
