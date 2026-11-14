using DiffWeave.App.Editor;
using DiffWeave.Core.Diff;

namespace DiffWeave.App.Documents;

/// <summary>
/// Decorations of an input pane derived from the alignment: line backgrounds per difference block, character-level
/// highlights (computed lazily for visible lines with the core <see cref="InlineDiffer"/>) and gap markers where the
/// other panes have lines this pane lacks.
/// </summary>
public sealed class AlignmentDecorationSource : IEditorDecorationSource
{
    private readonly Func<PaneMapping?> _mapping;
    private readonly int _pane;
    private readonly Func<int, int, int?> _partnerPane;
    private readonly Func<int, IEditorTextSource> _sources;
    private readonly Dictionary<int, IReadOnlyList<Core.Diff.TextRange>> _inlineCache = [];
    private InlineDiffOptions _inlineOptions = InlineDiffOptions.Default;

    /// <summary>Creates the source.</summary>
    /// <param name="mapping">Returns the current mapping.</param>
    /// <param name="pane">The pane (mapping index) decorated.</param>
    /// <param name="partnerPane">Given (row, pane) returns the pane to compare with for inline highlights, or null.</param>
    /// <param name="sources">Text of each pane.</param>
    public AlignmentDecorationSource(Func<PaneMapping?> mapping, int pane, Func<int, int, int?> partnerPane, Func<int, IEditorTextSource> sources)
    {
        _mapping = mapping;
        _pane = pane;
        _partnerPane = partnerPane;
        _sources = sources;
    }

    /// <inheritdoc />
    public event EventHandler? Invalidated;

    /// <summary>Kind override per block (e.g. resolution state), or null.</summary>
    public Func<DiffBlock, int, HighlightKind>? KindSelector { get; set; }

    /// <summary>Clears caches after the mapping changed.</summary>
    public void Invalidate(DiffOptions options)
    {
        _inlineCache.Clear();
        _inlineOptions = new InlineDiffOptions { Whitespace = options.Whitespace, IgnoreCase = options.IgnoreCase, MergeGap = 2 };
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void GetDecorations(int firstLine, int lastLine, DecorationSink sink)
    {
        if (_mapping() is not { } m)
        {
            return;
        }

        LineAlignment a = m.Alignment;
        IReadOnlyList<DiffBlock> blocks = m.Blocks;
        int lineCount = m.LineCount(_pane);

        // First block whose span in this pane ends at or after firstLine.
        int lo = 0, hi = blocks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            LineSpan s = blocks[mid].Spans[_pane];
            if (Math.Max(s.End, s.Start + 1) <= firstLine)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int bi = lo; bi < blocks.Count; bi++)
        {
            DiffBlock b = blocks[bi];
            LineSpan span = b.Spans[_pane];
            if (span.Start > lastLine)
            {
                break;
            }

            HighlightKind kind = KindSelector?.Invoke(b, _pane) ?? b.KindFor(_pane);
            if (span.Count == 0)
            {
                bool othersHaveLines = b.Spans.Any(s => s.Count > 0);
                if (othersHaveLines && kind != HighlightKind.None)
                {
                    sink.Gaps.Add(new GapMarker(span.Start, b.IsConflict ? HighlightKind.Conflict : DeletedOrKind(kind)));
                }

                continue;
            }

            if (kind == HighlightKind.None)
            {
                continue;
            }

            int from = Math.Max(firstLine, span.Start);
            int to = Math.Min(Math.Min(lastLine, span.End - 1), lineCount - 1);
            for (int line = from; line <= to; line++)
            {
                sink.Lines.Add(new LineDecoration(line, kind));
                foreach (Core.Diff.TextRange r in GetInline(a, line))
                {
                    sink.Inlines.Add(new InlineDecoration(line, r.Start, r.Length, kind));
                }
            }
        }
    }

    private static HighlightKind DeletedOrKind(HighlightKind k) => k;

    private IReadOnlyList<Core.Diff.TextRange> GetInline(LineAlignment a, int line)
    {
        if (_inlineCache.TryGetValue(line, out IReadOnlyList<Core.Diff.TextRange>? cached))
        {
            return cached;
        }

        IReadOnlyList<Core.Diff.TextRange> result = [];
        int row = a.GetRow(PaneToAlignment(_pane), line);
        if (a.GetRowKind(row) is not RowKind.Equal and not RowKind.Added and not RowKind.Removed && _partnerPane(row, _pane) is { } partner)
        {
            int otherLine = a.GetLine(row, PaneToAlignment(partner));
            if (otherLine >= 0)
            {
                string mine = _sources(_pane).GetLine(line);
                string theirs = _sources(partner).GetLine(otherLine);
                if (!string.Equals(mine, theirs, StringComparison.Ordinal))
                {
                    InlineDiffResult d = InlineDiffer.Diff(mine, theirs, _inlineOptions);
                    result = d.Left;
                }
            }
        }

        if (_inlineCache.Count > 20000)
        {
            _inlineCache.Clear();
        }

        _inlineCache[line] = result;
        return result;
    }

    /// <summary>Maps a mapping pane index to the alignment pane index (identity unless overridden).</summary>
    public Func<int, int> PaneToAlignment { get; set; } = p => p;
}
