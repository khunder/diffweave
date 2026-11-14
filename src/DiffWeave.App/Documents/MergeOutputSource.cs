using DiffWeave.App.Editor;
using DiffWeave.Core.Merge;

namespace DiffWeave.App.Documents;

/// <summary>
/// The editable merge OUTPUT pane text. Reads the current <see cref="MergeOutput"/>; every edit is applied with
/// <see cref="MergeSession.ApplyManualEdit"/> and undo/redo use the session's history, so hand edits, region choices
/// and accepted proposals share one undo stack.
/// </summary>
public sealed class MergeOutputSource : IEditorTextSource
{
    private readonly MergeSession _session;
    private MergeOutput _output;

    /// <summary>Creates the source.</summary>
    public MergeOutputSource(MergeSession session)
    {
        _session = session;
        _output = session.BuildOutput();
        session.Changed += OnSessionChanged;
    }

    /// <inheritdoc />
    public event EventHandler<TextSourceChangedEventArgs>? Changed;

    /// <summary>The current output.</summary>
    public MergeOutput Output => _output;

    /// <inheritdoc />
    public int LineCount => _output.Lines.Count;

    /// <inheritdoc />
    public bool IsEditable => true;

    /// <inheritdoc />
    public bool CanUndo => _session.CanUndo;

    /// <inheritdoc />
    public bool CanRedo => _session.CanRedo;

    /// <inheritdoc />
    public string GetLine(int line) => _output.Lines[line];

    /// <inheritdoc />
    public TextPos Replace(TextRange range, string text)
    {
        int start, removed;
        string prefix, suffix;
        if (LineCount == 0)
        {
            start = 0;
            removed = 0;
            prefix = suffix = string.Empty;
        }
        else
        {
            TextPos s = this.Clamp(range.Start);
            TextPos e = this.Clamp(range.End);
            start = s.Line;
            removed = e.Line - s.Line + 1;
            prefix = GetLine(s.Line)[..s.Column];
            suffix = GetLine(e.Line)[e.Column..];
        }

        string[] parts = TextSourceExtensions.SplitLines(text);
        parts[0] = prefix + parts[0];
        TextPos end = new(start + parts.Length - 1, parts[^1].Length);
        parts[^1] += suffix;
        _session.ApplyManualEdit(start, removed, parts);
        return end;
    }

    /// <inheritdoc />
    public TextPos? Undo() => _session.Undo() ? CaretAfterChange() : null;

    /// <inheritdoc />
    public TextPos? Redo() => _session.Redo() ? CaretAfterChange() : null;

    private TextPos? _lastChangePos;

    private TextPos? CaretAfterChange() => _lastChangePos;

    private void OnSessionChanged(object? sender, MergeChangedEventArgs e)
    {
        _output = _session.BuildOutput();
        if (e.Regions.Count > 0)
        {
            Core.Diff.LineRange r = _output.GetOutputRange(e.Regions[0]);
            _lastChangePos = new TextPos(Math.Min(r.Start, Math.Max(0, LineCount - 1)), 0);
        }

        Changed?.Invoke(this, TextSourceChangedEventArgs.Reset);
    }
}
