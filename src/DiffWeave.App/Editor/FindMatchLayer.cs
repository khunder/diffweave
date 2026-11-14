using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;

namespace DiffWeave.App.Editor;

/// <summary>Highlights every match of <see cref="Controls.DiffTextView.FindHighlight"/> on visible lines.</summary>
public sealed class FindMatchLayer : IEditorLayer
{
    private FindOptions? _cachedOptions;
    private Regex? _regex;

    /// <inheritdoc />
    public void Render(DrawingContext context, EditorRenderContext frame)
    {
        FindOptions? options = frame.View.FindHighlight;
        if (options is null || frame.View.Source is not { } source)
        {
            return;
        }

        if (!ReferenceEquals(options, _cachedOptions))
        {
            _cachedOptions = options;
            _regex = options.TryBuildRegex();
        }

        if (_regex is null)
        {
            return;
        }

        IBrush brush = frame.Palette.InlineBrush(HighlightKind.FindMatch);
        IPen pen = new Pen(frame.Palette.MarkBrush(HighlightKind.FindMatch), 1);
        int lastLine = -1;
        foreach (VisualRow row in frame.Rows)
        {
            if (row.Line == lastLine)
            {
                continue;
            }

            lastLine = row.Line;
            string text = source.GetLine(row.Line);
            try
            {
                foreach (Match m in _regex.Matches(text))
                {
                    if (m.Length == 0)
                    {
                        continue;
                    }

                    foreach (VisualRow r in frame.RowsOfLine(row.Line))
                    {
                        int s = Math.Max(m.Index, r.StartColumn);
                        int e = Math.Min(m.Index + m.Length, r.EndColumn);
                        if (e <= s)
                        {
                            continue;
                        }

                        double x1 = frame.ColumnToX(r, s);
                        double x2 = frame.ColumnToX(r, e);
                        context.DrawRectangle(brush, pen, new Rect(x1, r.Top + 1, x2 - x1, frame.RowHeight - 2), 2, 2);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return;
            }
        }
    }
}
