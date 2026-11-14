using Avalonia;
using Avalonia.Media;
using DiffWeave.App.Controls;

namespace DiffWeave.App.Editor;

/// <summary>A visible row of the text area (one row per line, or several when word wrap is on).</summary>
public readonly record struct VisualRow(int Row, int Line, int Segment, int StartColumn, int EndColumn, double Top);

/// <summary>Everything a layer needs to paint one frame. Built once per frame by <see cref="DiffTextView"/>.</summary>
public sealed class EditorRenderContext
{
    internal EditorRenderContext(DiffTextView view, EditorPalette palette, DecorationSink decorations, IReadOnlyList<VisualRow> rows)
    {
        View = view;
        Palette = palette;
        Decorations = decorations;
        Rows = rows;
    }

    /// <summary>The control being rendered.</summary>
    public DiffTextView View { get; }

    /// <summary>Theme brushes.</summary>
    public EditorPalette Palette { get; }

    /// <summary>Decorations for the visible lines, collected from all decoration sources.</summary>
    public DecorationSink Decorations { get; }

    /// <summary>The visible rows from top to bottom.</summary>
    public IReadOnlyList<VisualRow> Rows { get; }

    /// <summary>Height of a row in pixels.</summary>
    public double RowHeight => View.RowHeight;

    /// <summary>Left edge of the text area in control coordinates.</summary>
    public double TextLeft => View.TextAreaLeft;

    /// <summary>Width of the control.</summary>
    public double Width => View.Bounds.Width;

    /// <summary>Returns the x coordinate (control space) of a source column on the given row.</summary>
    public double ColumnToX(VisualRow row, int column) => View.ColumnToX(row, column);

    /// <summary>Enumerates the rows that display <paramref name="line"/>.</summary>
    public IEnumerable<VisualRow> RowsOfLine(int line)
    {
        foreach (VisualRow r in Rows)
        {
            if (r.Line == line)
            {
                yield return r;
            }
        }
    }
}

/// <summary>
/// One paint layer of the editor. Layers are painted in order (background, inline highlights, text, selection, caret)
/// and plugins can insert additional layers through <see cref="DiffTextView.Layers"/>.
/// </summary>
public interface IEditorLayer
{
    /// <summary>Paints the layer clipped to the text area.</summary>
    void Render(DrawingContext context, EditorRenderContext frame);
}

/// <summary>Paints line backgrounds, the current line, gap markers and the current-region frame.</summary>
public sealed class BackgroundLayer : IEditorLayer
{
    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        double left = frame.TextLeft;
        double width = Math.Max(0, frame.Width - left);
        Dictionary<int, HighlightKind> lineKinds = [];
        foreach (LineDecoration d in frame.Decorations.Lines)
        {
            if (!lineKinds.TryGetValue(d.Line, out HighlightKind k) || Priority(d.Kind) > Priority(k))
            {
                lineKinds[d.Line] = d.Kind;
            }
        }

        int caretLine = frame.View.CaretPosition.Line;
        bool showCurrentLine = frame.View.IsFocused && frame.View.SelectionRange.IsEmpty;
        foreach (VisualRow row in frame.Rows)
        {
            Rect r = new(left, row.Top, width, frame.RowHeight);
            if (lineKinds.TryGetValue(row.Line, out HighlightKind kind) && kind != HighlightKind.None)
            {
                context.FillRectangle(frame.Palette.LineBrush(kind), r);
            }

            if (showCurrentLine && row.Line == caretLine)
            {
                context.FillRectangle(frame.Palette.CurrentLine, r);
            }
        }

        foreach (GapMarker gap in frame.Decorations.Gaps)
        {
            double y = frame.View.GetLineTop(gap.BeforeLine);
            context.FillRectangle(frame.Palette.MarkBrush(gap.Kind), new Rect(left, y - 1, width, 2));
        }

        LineSpan? region = frame.View.CurrentRegion;
        if (region is { } span)
        {
            double top = frame.View.GetLineTop(span.Start);
            double bottom = span.Count == 0 ? top : frame.View.GetLineTop(span.Start + span.Count);
            Pen pen = new(frame.Palette.CurrentRegion, 1.5);
            if (span.Count == 0)
            {
                context.DrawLine(new Pen(frame.Palette.CurrentRegion, 3), new Point(left, top), new Point(left + width, top));
            }
            else
            {
                context.DrawRectangle(null, pen, new Rect(left + 0.75, top + 0.75, Math.Max(0, width - 1.5), Math.Max(0, bottom - top - 1.5)));
            }
        }
    }

    private static int Priority(HighlightKind k) => k switch
    {
        HighlightKind.Conflict => 10,
        HighlightKind.Proposal => 9,
        HighlightKind.Resolved => 8,
        _ => 1,
    };
}

/// <summary>Paints character-level highlights (word/char diff, plugin ranges, find matches).</summary>
public sealed class InlineHighlightLayer : IEditorLayer
{
    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        foreach (InlineDecoration d in frame.Decorations.Inlines)
        {
            foreach (VisualRow row in frame.RowsOfLine(d.Line))
            {
                int s = Math.Max(d.StartColumn, row.StartColumn);
                int e = Math.Min(d.StartColumn + d.Length, row.EndColumn);
                if (e < s || (e == s && d.Length > 0))
                {
                    continue;
                }

                double x1 = frame.ColumnToX(row, s);
                double x2 = frame.ColumnToX(row, e);
                if (x2 - x1 < 2)
                {
                    x2 = x1 + 2;
                }

                Rect r = new(x1, row.Top + 1, x2 - x1, frame.RowHeight - 2);
                switch (d.Style)
                {
                    case InlineStyle.Fill:
                        context.DrawRectangle(frame.Palette.InlineBrush(d.Kind), null, r, 2, 2);
                        break;
                    case InlineStyle.Underline:
                        context.FillRectangle(frame.Palette.MarkBrush(d.Kind), new Rect(x1, row.Top + frame.RowHeight - 2.5, x2 - x1, 1.5));
                        break;
                    case InlineStyle.Box:
                        context.DrawRectangle(null, new Pen(frame.Palette.MarkBrush(d.Kind), 1), r.Deflate(0.5), 2, 2);
                        break;
                }
            }
        }
    }
}

/// <summary>Paints the text of each visible row.</summary>
public sealed class TextLayer : IEditorLayer
{
    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        foreach (VisualRow row in frame.Rows)
        {
            FormattedText? text = frame.View.GetRowText(row);
            if (text is null)
            {
                continue;
            }

            double x = frame.ColumnToX(row, row.StartColumn);
            double y = row.Top + ((frame.RowHeight - text.Height) / 2);
            context.DrawText(text, new Point(x, y));
        }
    }
}

/// <summary>Paints the selection.</summary>
public sealed class SelectionLayer : IEditorLayer
{
    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        TextRange sel = frame.View.SelectionRange;
        if (sel.IsEmpty)
        {
            return;
        }

        IBrush brush = frame.View.IsFocused ? frame.Palette.Selection : frame.Palette.SelectionInactive;
        foreach (VisualRow row in frame.Rows)
        {
            if (row.Line < sel.Start.Line || row.Line > sel.End.Line)
            {
                continue;
            }

            int s = row.Line == sel.Start.Line ? Math.Max(sel.Start.Column, row.StartColumn) : row.StartColumn;
            int e = row.Line == sel.End.Line ? Math.Min(sel.End.Column, row.EndColumn) : row.EndColumn;
            if (row.Line == sel.Start.Line && sel.Start.Column > row.EndColumn)
            {
                continue;
            }

            if (row.Line == sel.End.Line && sel.End.Column < row.StartColumn)
            {
                continue;
            }

            double x1 = frame.ColumnToX(row, s);
            double x2 = frame.ColumnToX(row, e);
            bool lineBreakSelected = row.Line < sel.End.Line && row.EndColumn == frame.View.LineLength(row.Line);
            if (lineBreakSelected)
            {
                x2 += frame.View.CharWidth * 0.6;
            }

            if (x2 > x1)
            {
                context.FillRectangle(brush, new Rect(x1, row.Top, x2 - x1, frame.RowHeight));
            }
        }
    }
}

/// <summary>Paints the caret when the control is focused and the caret blink phase is visible.</summary>
public sealed class CaretLayer : IEditorLayer
{
    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        if (!frame.View.IsFocused || !frame.View.CaretVisible)
        {
            return;
        }

        TextPos caret = frame.View.CaretPosition;
        foreach (VisualRow row in frame.RowsOfLine(caret.Line))
        {
            bool lastSegment = row.EndColumn == frame.View.LineLength(row.Line);
            if (caret.Column < row.StartColumn || caret.Column > row.EndColumn || (caret.Column == row.EndColumn && !lastSegment))
            {
                continue;
            }

            double x = Math.Round(frame.ColumnToX(row, caret.Column));
            double w = frame.View.IsReadOnly ? 1 : 2;
            context.FillRectangle(frame.Palette.Caret, new Rect(x, row.Top + 1, w, frame.RowHeight - 2));
            break;
        }
    }
}
