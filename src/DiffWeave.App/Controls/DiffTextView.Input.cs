using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>Caret, selection, keyboard, mouse, clipboard, find and undo handling.</summary>
public partial class DiffTextView
{
    private TextPos _caret;
    private TextPos _anchor;
    private double? _desiredX;
    private bool _selecting;
    private SelectionUnit _selectionUnit;

    private enum SelectionUnit
    {
        Character,
        Word,
        Line,
    }

    /// <summary>Raised when the view wants to show a status message (e.g. "pattern not found").</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>The caret position.</summary>
    public TextPos CaretPosition => _caret;

    /// <summary>The (normalized) selection.</summary>
    public TextRange SelectionRange => TextRange.FromPoints(_anchor, _caret);

    /// <summary>The selected text ('\n' separated).</summary>
    public string SelectedText => Source?.GetText(SelectionRange) ?? string.Empty;

    /// <summary>Moves the caret, optionally extending the selection, and scrolls it into view.</summary>
    public void SetCaret(TextPos pos, bool extendSelection = false)
    {
        if (Source is null)
        {
            return;
        }

        _caret = Source.Clamp(pos);
        if (!extendSelection)
        {
            _anchor = _caret;
        }

        CaretVisible = true;
        EnsureCaretVisible();
        InvalidateVisual();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a range; the caret goes to its end.</summary>
    public void Select(TextRange range)
    {
        if (Source is null)
        {
            return;
        }

        _anchor = Source.Clamp(range.Start);
        SetCaret(range.End, true);
    }

    /// <summary>Selects everything.</summary>
    public void SelectAll()
    {
        if (Source is null)
        {
            return;
        }

        _anchor = default;
        SetCaret(Source.EndPosition(), true);
    }

    /// <summary>Moves the caret to the start of a (zero-based) line and centres it.</summary>
    public void GoToLine(int line)
    {
        if (Source is null || LineCount == 0)
        {
            return;
        }

        line = Math.Clamp(line, 0, LineCount - 1);
        CenterLine(line);
        SetCaret(new TextPos(line, 0));
    }

    /// <summary>Scrolls so that the caret is visible.</summary>
    public void EnsureCaretVisible()
    {
        if (Bounds.Height <= 0)
        {
            return;
        }

        double rh = RowHeight;
        double row = LineToRow(_caret.Line);
        if (WordWrap && LineCount > 0)
        {
            DisplayLine d = GetDisplayLine(_caret.Line);
            int segments = (int)Math.Round(LineToRow(_caret.Line + 1) - row);
            row += Math.Min(d.ToDisplay(_caret.Column) / Math.Max(1, _wrapColumns), Math.Max(0, segments - 1));
        }

        double top = row * rh;
        if (top < _verticalOffset)
        {
            VerticalOffset = top;
        }
        else if (top + rh > _verticalOffset + ViewportHeight)
        {
            VerticalOffset = top + rh - ViewportHeight;
        }

        if (!WordWrap && Source is not null && LineCount > 0)
        {
            double x = GetDisplayLine(_caret.Line).ToDisplay(_caret.Column) * CharWidth;
            double view = ViewportWidth - (CharWidth * 2);
            if (x < _horizontalOffset)
            {
                HorizontalOffset = Math.Max(0, x - (CharWidth * 4));
            }
            else if (x > _horizontalOffset + view)
            {
                HorizontalOffset = x - view + (CharWidth * 4);
            }
        }
    }

    /// <summary>Finds the next (or previous) match starting at the caret, wrapping around. Returns true when found.</summary>
    public bool Find(FindOptions options, bool backwards = false)
    {
        if (Source is not { } src || src.LineCount == 0 || options.TryBuildRegex() is not { } regex)
        {
            return false;
        }

        TextRange sel = SelectionRange;
        int n = src.LineCount;
        if (!backwards)
        {
            TextPos from = sel.End;
            for (int i = 0; i <= n; i++)
            {
                int line = (from.Line + i) % n;
                string text = src.GetLine(line);
                int start = i == 0 ? Math.Min(from.Column, text.Length) : 0;
                System.Text.RegularExpressions.Match m = regex.Match(text, start);
                while (m.Success && m.Length == 0)
                {
                    m = m.NextMatch();
                }

                if (m.Success && (i < n || m.Index < from.Column))
                {
                    SelectAndReveal(line, m.Index, m.Length);
                    return true;
                }
            }
        }
        else
        {
            TextPos from = sel.Start;
            for (int i = 0; i <= n; i++)
            {
                int line = (((from.Line - i) % n) + n) % n;
                string text = src.GetLine(line);
                int limit = i == 0 ? from.Column : int.MaxValue;
                System.Text.RegularExpressions.Match? best = null;
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
                {
                    if (m.Length > 0 && m.Index + m.Length <= limit)
                    {
                        best = m;
                    }
                }

                if (best is not null)
                {
                    SelectAndReveal(line, best.Index, best.Length);
                    return true;
                }
            }
        }

        StatusMessage?.Invoke(this, $"'{options.Pattern}' not found");
        return false;
    }

    private void SelectAndReveal(int line, int column, int length)
    {
        CenterLine(line);
        Select(new TextRange(new TextPos(line, column), new TextPos(line, column + length)));
    }

    /// <summary>Copies the selection to the clipboard.</summary>
    public async Task CopyAsync()
    {
        string text = SelectedText;
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        }
    }

    /// <summary>Cuts the selection to the clipboard.</summary>
    public async Task CutAsync()
    {
        if (!CanEdit || SelectionRange.IsEmpty)
        {
            return;
        }

        await CopyAsync();
        ReplaceSelection(string.Empty);
    }

    /// <summary>Pastes clipboard text at the caret.</summary>
    public async Task PasteAsync()
    {
        if (!CanEdit || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        string? text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            ReplaceSelection(text);
        }
    }

    /// <summary>Replaces the selection with text (typing). No-op when not editable.</summary>
    public void ReplaceSelection(string text)
    {
        if (!CanEdit)
        {
            return;
        }

        TextPos end = Source!.Replace(SelectionRange, text.ReplaceLineEndings("\n"));
        _desiredX = null;
        SetCaret(end);
    }

    /// <summary>Undoes the last edit of the source.</summary>
    public void Undo()
    {
        if (CanEdit && Source!.CanUndo && Source.Undo() is { } pos)
        {
            SetCaret(pos);
        }
    }

    /// <summary>Redoes the last undone edit of the source.</summary>
    public void Redo()
    {
        if (CanEdit && Source!.CanRedo && Source.Redo() is { } pos)
        {
            SetCaret(pos);
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || !CanEdit || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        string text = e.Text;
        if (text.Any(c => char.IsControl(c) && c != '\t'))
        {
            return;
        }

        ReplaceSelection(text);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Source is null)
        {
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (alt)
        {
            return;
        }

        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left when !ctrl: MoveHorizontal(-1, shift, false); break;
            case Key.Right when !ctrl: MoveHorizontal(1, shift, false); break;
            case Key.Left: MoveHorizontal(-1, shift, true); break;
            case Key.Right: MoveHorizontal(1, shift, true); break;
            case Key.Up when !ctrl: MoveVertical(-1, shift); break;
            case Key.Down when !ctrl: MoveVertical(1, shift); break;
            case Key.PageUp when !ctrl: MoveVertical(-Math.Max(1, VisibleLineCount - 1), shift); break;
            case Key.PageDown when !ctrl: MoveVertical(Math.Max(1, VisibleLineCount - 1), shift); break;
            case Key.Home when ctrl: SetCaret(default, shift); break;
            case Key.End when ctrl: SetCaret(Source.EndPosition(), shift); break;
            case Key.Home: SmartHome(shift); break;
            case Key.End: SetCaret(new TextPos(_caret.Line, LineLength(_caret.Line)), shift); _desiredX = null; break;
            case Key.A when ctrl && !shift: SelectAll(); break;
            case Key.C when ctrl && !shift: _ = CopyAsync(); break;
            case Key.Insert when ctrl: _ = CopyAsync(); break;
            case Key.X when ctrl && !shift && CanEdit: _ = CutAsync(); break;
            case Key.V when ctrl && !shift && CanEdit: _ = PasteAsync(); break;
            case Key.Insert when shift && CanEdit: _ = PasteAsync(); break;
            case Key.Z when ctrl && !shift && CanEdit: Undo(); break;
            case Key.Z when ctrl && shift && CanEdit: Redo(); break;
            case Key.Y when ctrl && !shift && CanEdit: Redo(); break;
            case Key.Enter when !ctrl && CanEdit: ReplaceSelection("\n" + LeadingWhitespace(_caret.Line, _caret.Column)); break;
            case Key.Tab when !ctrl && CanEdit && !shift: ReplaceSelection("\t"); break;
            case Key.Back when CanEdit: DeleteBackward(ctrl); break;
            case Key.Delete when CanEdit && !shift: DeleteForward(ctrl); break;
            case Key.Delete when CanEdit && shift: _ = CutAsync(); break;
            default: e.Handled = false; break;
        }
    }

    private string LeadingWhitespace(int line, int maxColumn)
    {
        string text = Source!.GetLine(line);
        int i = 0;
        while (i < text.Length && i < maxColumn && text[i] is ' ' or '\t')
        {
            i++;
        }

        return text[..i];
    }

    private void SmartHome(bool shift)
    {
        string text = Source!.GetLine(_caret.Line);
        int indent = 0;
        while (indent < text.Length && char.IsWhiteSpace(text[indent]))
        {
            indent++;
        }

        int target = _caret.Column == indent ? 0 : indent;
        _desiredX = null;
        SetCaret(new TextPos(_caret.Line, target), shift);
    }

    private void DeleteBackward(bool word)
    {
        if (!SelectionRange.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        TextPos from = word ? WordBoundary(_caret, -1) : Step(_caret, -1);
        if (from != _caret)
        {
            _anchor = from;
            ReplaceSelection(string.Empty);
        }
    }

    private void DeleteForward(bool word)
    {
        if (!SelectionRange.IsEmpty)
        {
            ReplaceSelection(string.Empty);
            return;
        }

        TextPos to = word ? WordBoundary(_caret, 1) : Step(_caret, 1);
        if (to != _caret)
        {
            _anchor = to;
            ReplaceSelection(string.Empty);
        }
    }

    private TextPos Step(TextPos pos, int dir)
    {
        if (dir < 0)
        {
            if (pos.Column > 0)
            {
                return pos with { Column = pos.Column - 1 };
            }

            return pos.Line > 0 ? new TextPos(pos.Line - 1, LineLength(pos.Line - 1)) : pos;
        }

        if (pos.Column < LineLength(pos.Line))
        {
            return pos with { Column = pos.Column + 1 };
        }

        return pos.Line < LineCount - 1 ? new TextPos(pos.Line + 1, 0) : pos;
    }

    private static int CharClass(char c) => char.IsWhiteSpace(c) ? 0 : char.IsLetterOrDigit(c) || c == '_' ? 1 : 2;

    private TextPos WordBoundary(TextPos pos, int dir)
    {
        string text = Source!.GetLine(pos.Line);
        if (dir < 0)
        {
            if (pos.Column == 0)
            {
                return Step(pos, -1);
            }

            int i = pos.Column;
            while (i > 0 && char.IsWhiteSpace(text[i - 1]))
            {
                i--;
            }

            if (i > 0)
            {
                int cls = CharClass(text[i - 1]);
                while (i > 0 && CharClass(text[i - 1]) == cls)
                {
                    i--;
                }
            }

            return pos with { Column = i };
        }
        else
        {
            if (pos.Column >= text.Length)
            {
                return Step(pos, 1);
            }

            int i = pos.Column;
            int cls = CharClass(text[i]);
            while (i < text.Length && CharClass(text[i]) == cls)
            {
                i++;
            }

            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return pos with { Column = i };
        }
    }

    private void MoveHorizontal(int dir, bool shift, bool word)
    {
        _desiredX = null;
        if (!shift && !SelectionRange.IsEmpty && !word)
        {
            SetCaret(dir < 0 ? SelectionRange.Start : SelectionRange.End);
            return;
        }

        SetCaret(word ? WordBoundary(_caret, dir) : Step(_caret, dir), shift);
    }

    private void MoveVertical(int lines, bool shift)
    {
        if (LineCount == 0)
        {
            return;
        }

        DisplayLine d = GetDisplayLine(_caret.Line);
        double x = _desiredX ?? d.ToDisplay(_caret.Column);
        int target = Math.Clamp(_caret.Line + lines, 0, LineCount - 1);
        int col = GetDisplayLine(target).ToSource((int)Math.Round(x));
        col = Math.Min(col, LineLength(target));
        if (target == _caret.Line && lines != 0)
        {
            col = lines < 0 ? 0 : LineLength(target);
        }

        SetCaret(new TextPos(target, col), shift);
        _desiredX = x;
    }

    /// <summary>Converts a control-space point to a document position.</summary>
    public TextPos HitTest(Point p)
    {
        if (Source is null || LineCount == 0)
        {
            return default;
        }

        double rh = RowHeight;
        int row = (int)Math.Floor((p.Y + _verticalOffset) / rh);
        int rowCount = RowCount;
        if (row >= rowCount)
        {
            return Source.EndPosition();
        }

        row = Math.Max(0, row);
        (int line, int segStartDisplay, bool phantom) = LocateRow(row);
        if (phantom)
        {
            return new TextPos(line, 0);
        }

        DisplayLine d = GetDisplayLine(line);
        int displayCol = segStartDisplay + (int)Math.Round((p.X - TextAreaLeft + _horizontalOffset) / CharWidth);
        if (WordWrap)
        {
            displayCol = Math.Min(displayCol, segStartDisplay + _wrapColumns);
        }

        int col = d.ToSource(Math.Max(0, displayCol));
        return new TextPos(line, Math.Min(col, LineLength(line)));
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint pt = e.GetCurrentPoint(this);
        Focus();
        if (!pt.Properties.IsLeftButtonPressed || Source is null)
        {
            return;
        }

        TextPos pos = HitTest(pt.Position);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _desiredX = null;
        if (pt.Position.X < GutterWidth)
        {
            _selectionUnit = SelectionUnit.Line;
            SelectLines(pos.Line, pos.Line, shift);
        }
        else if (e.ClickCount == 2)
        {
            _selectionUnit = SelectionUnit.Word;
            (int s, int en) = WordAt(pos);
            _anchor = pos with { Column = s };
            SetCaret(pos with { Column = en }, true);
        }
        else if (e.ClickCount >= 3)
        {
            _selectionUnit = SelectionUnit.Line;
            SelectLines(pos.Line, pos.Line, false);
        }
        else
        {
            _selectionUnit = SelectionUnit.Character;
            SetCaret(pos, shift);
        }

        _selecting = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void SelectLines(int from, int to, bool extend)
    {
        int anchorLine = extend ? _anchor.Line : from;
        int a = Math.Min(anchorLine, to), b = Math.Max(anchorLine, to);
        _anchor = new TextPos(a, 0);
        TextPos end = b + 1 < LineCount ? new TextPos(b + 1, 0) : new TextPos(b, LineLength(b));
        SetCaret(end, true);
        if (to < anchorLine)
        {
            (_anchor, _caret) = (_caret, new TextPos(a, 0));
            InvalidateVisual();
        }
    }

    private (int Start, int End) WordAt(TextPos pos)
    {
        string text = Source!.GetLine(pos.Line);
        if (text.Length == 0)
        {
            return (0, 0);
        }

        int c = Math.Min(pos.Column, text.Length - 1);
        int cls = CharClass(text[c]);
        int s = c, en = c;
        while (s > 0 && CharClass(text[s - 1]) == cls)
        {
            s--;
        }

        while (en < text.Length && CharClass(text[en]) == cls)
        {
            en++;
        }

        return (s, en);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting || Source is null)
        {
            return;
        }

        Point p = e.GetPosition(this);
        TextPos pos = HitTest(p);
        switch (_selectionUnit)
        {
            case SelectionUnit.Line:
                int anchorLine = _anchor.Line;
                if (pos.Line >= anchorLine)
                {
                    TextPos end = pos.Line + 1 < LineCount ? new TextPos(pos.Line + 1, 0) : new TextPos(pos.Line, LineLength(pos.Line));
                    SetCaret(end, true);
                }
                else
                {
                    SetCaret(new TextPos(pos.Line, 0), true);
                }

                break;
            case SelectionUnit.Word:
                (int s, int en) = WordAt(pos);
                SetCaret(pos < _anchor ? pos with { Column = s } : pos with { Column = en }, true);
                break;
            default:
                SetCaret(pos, true);
                break;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        double rh = RowHeight;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            double dx = Math.Abs(e.Delta.X) > 0 ? e.Delta.X : e.Delta.Y;
            HorizontalOffset -= dx * CharWidth * 6;
        }
        else
        {
            VerticalOffset -= e.Delta.Y * rh * 3;
        }

        e.Handled = true;
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem copy = new() { Header = "_Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => _ = CopyAsync();
        MenuItem cut = new() { Header = "Cu_t", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        cut.Click += (_, _) => _ = CutAsync();
        MenuItem paste = new() { Header = "_Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        paste.Click += (_, _) => _ = PasteAsync();
        MenuItem selectAll = new() { Header = "Select _All", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += (_, _) => SelectAll();
        ContextMenu menu = new() { ItemsSource = new Control[] { copy, cut, paste, new Separator(), selectAll } };
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = !SelectionRange.IsEmpty;
            cut.IsEnabled = CanEdit && !SelectionRange.IsEmpty;
            paste.IsEnabled = CanEdit;
        };
        return menu;
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new DiffTextViewAutomationPeer(this);
}

/// <summary>Automation peer exposing the editor as a document.</summary>
public sealed class DiffTextViewAutomationPeer(DiffTextView owner) : ControlAutomationPeer(owner)
{
    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(DiffTextView);

    /// <inheritdoc />
    protected override bool IsContentElementCore() => true;

    /// <inheritdoc />
    protected override bool IsControlElementCore() => true;
}
