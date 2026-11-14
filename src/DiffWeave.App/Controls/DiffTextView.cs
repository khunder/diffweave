using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>
/// A virtualized monospace text view/editor purpose-built for diff and merge panes.
/// Only visible rows are realized; per-row text layouts are cached. Painting is split into ordered
/// <see cref="IEditorLayer"/>s fed by <see cref="IEditorDecorationSource"/>s, and a gutter shows line numbers,
/// change markers and plugin decoration marks.
/// </summary>
public partial class DiffTextView : Control
{
    /// <summary>The displayed document.</summary>
    public static readonly StyledProperty<IEditorTextSource?> SourceProperty =
        AvaloniaProperty.Register<DiffTextView, IEditorTextSource?>(nameof(Source));

    /// <summary>Editor font family.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<DiffTextView, FontFamily>(nameof(FontFamily), FontFamily.Parse(DefaultFontFamily));

    /// <summary>Editor font size.</summary>
    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DiffTextView, double>(nameof(FontSize), 13);

    /// <summary>Tab width in columns.</summary>
    public static readonly StyledProperty<int> TabSizeProperty =
        AvaloniaProperty.Register<DiffTextView, int>(nameof(TabSize), 4, coerce: (_, v) => Math.Clamp(v, 1, 16));

    /// <summary>Wraps long lines at the view width.</summary>
    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<DiffTextView, bool>(nameof(WordWrap));

    /// <summary>Shows spaces and tabs as visible glyphs.</summary>
    public static readonly StyledProperty<bool> ShowWhitespaceProperty =
        AvaloniaProperty.Register<DiffTextView, bool>(nameof(ShowWhitespace));

    /// <summary>Prevents editing even if the source is editable.</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DiffTextView, bool>(nameof(IsReadOnly), true);

    /// <summary>Shows the line-number column.</summary>
    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<DiffTextView, bool>(nameof(ShowLineNumbers), true);

    /// <summary>Lines framed as the current difference region.</summary>
    public static readonly StyledProperty<LineSpan?> CurrentRegionProperty =
        AvaloniaProperty.Register<DiffTextView, LineSpan?>(nameof(CurrentRegion));

    /// <summary>Highlights all matches of this query in the visible area.</summary>
    public static readonly StyledProperty<FindOptions?> FindHighlightProperty =
        AvaloniaProperty.Register<DiffTextView, FindOptions?>(nameof(FindHighlight));

    /// <summary>The default monospace font list.</summary>
    public const string DefaultFontFamily = "Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Liberation Mono, Consolas, Menlo, monospace";

    private const double TextPadding = 6;
    private const double MarkerWidth = 12;
    private const int MaxCachedLayouts = 4000;

    private readonly EditorPalette _palette;
    private readonly DecorationSink _sink = new();
    private readonly Dictionary<int, DisplayLine> _displayCache = [];
    private readonly Dictionary<(int Line, int Segment), FormattedText> _textCache = [];
    private readonly DispatcherTimer _caretTimer;

    private Typeface _typeface;
    private double _charWidth = 8;
    private double _rowHeight = 18;
    private bool _metricsValid;
    private int _maxDisplayLength;
    private double _verticalOffset;
    private double _horizontalOffset;
    private Size _lastSize;

    static DiffTextView()
    {
        FocusableProperty.OverrideDefaultValue<DiffTextView>(true);
        ClipToBoundsProperty.OverrideDefaultValue<DiffTextView>(true);
        AffectsRender<DiffTextView>(CurrentRegionProperty, FindHighlightProperty, ShowLineNumbersProperty);
    }

    /// <summary>Creates the view.</summary>
    public DiffTextView()
    {
        _palette = new EditorPalette(this);
        _typeface = new Typeface(FontFamily);
        Layers = [new BackgroundLayer(), new InlineHighlightLayer(), new FindMatchLayer(), new TextLayer(), new SelectionLayer(), new CaretLayer()];
        Layers.CollectionChanged += (_, _) => InvalidateVisual();
        DecorationSources.CollectionChanged += OnDecorationSourcesChanged;
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            CaretVisible = !CaretVisible;
            InvalidateVisual();
        };
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Ibeam);
        ActualThemeVariantChanged += (_, _) => InvalidateTheme();
        ResourcesChanged += (_, _) => InvalidateTheme();
        ContextMenu = BuildContextMenu();
    }

    /// <summary>Raised when the scroll offsets, extent or viewport changed.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Raised when the caret moved or the selection changed.</summary>
    public event EventHandler? CaretMoved;

    /// <inheritdoc cref="SourceProperty"/>
    public IEditorTextSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc cref="FontFamilyProperty"/>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <inheritdoc cref="FontSizeProperty"/>
    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <inheritdoc cref="TabSizeProperty"/>
    public int TabSize
    {
        get => GetValue(TabSizeProperty);
        set => SetValue(TabSizeProperty, value);
    }

    /// <inheritdoc cref="WordWrapProperty"/>
    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <inheritdoc cref="ShowWhitespaceProperty"/>
    public bool ShowWhitespace
    {
        get => GetValue(ShowWhitespaceProperty);
        set => SetValue(ShowWhitespaceProperty, value);
    }

    /// <inheritdoc cref="IsReadOnlyProperty"/>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <inheritdoc cref="ShowLineNumbersProperty"/>
    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    /// <inheritdoc cref="CurrentRegionProperty"/>
    public LineSpan? CurrentRegion
    {
        get => GetValue(CurrentRegionProperty);
        set => SetValue(CurrentRegionProperty, value);
    }

    /// <inheritdoc cref="FindHighlightProperty"/>
    public FindOptions? FindHighlight
    {
        get => GetValue(FindHighlightProperty);
        set => SetValue(FindHighlightProperty, value);
    }

    /// <summary>Paint layers in z-order. Plugins may insert custom layers.</summary>
    public ObservableCollection<IEditorLayer> Layers { get; }

    /// <summary>Decoration sources (diff model, plugins). Queried for visible lines only.</summary>
    public ObservableCollection<IEditorDecorationSource> DecorationSources { get; } = [];

    /// <summary>True when the source accepts edits and the view is not read-only.</summary>
    public bool CanEdit => !IsReadOnly && Source is { IsEditable: true };

    /// <summary>Number of lines in the source.</summary>
    public int LineCount => Source?.LineCount ?? 0;

    /// <summary>Height of one visual row.</summary>
    public double RowHeight
    {
        get
        {
            EnsureMetrics();
            return _rowHeight;
        }
    }

    /// <summary>Width of one character cell.</summary>
    public double CharWidth
    {
        get
        {
            EnsureMetrics();
            return _charWidth;
        }
    }

    /// <summary>Width of the gutter (line numbers + markers).</summary>
    public double GutterWidth
    {
        get
        {
            EnsureMetrics();
            double numbers = ShowLineNumbers ? (Math.Max(3, DigitCount(LineCount)) * _charWidth) + 10 : 0;
            return numbers + MarkerWidth + 1;
        }
    }

    /// <summary>Left edge of the text area.</summary>
    public double TextAreaLeft => GutterWidth + TextPadding;

    /// <summary>Vertical scroll offset in pixels.</summary>
    public double VerticalOffset
    {
        get => _verticalOffset;
        set
        {
            double v = Math.Clamp(value, 0, Math.Max(0, ExtentHeight - ViewportHeight));
            if (Math.Abs(v - _verticalOffset) > 0.01)
            {
                _verticalOffset = v;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Horizontal scroll offset in pixels.</summary>
    public double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            double v = WordWrap ? 0 : Math.Clamp(value, 0, Math.Max(0, ExtentWidth - ViewportWidth));
            if (Math.Abs(v - _horizontalOffset) > 0.01)
            {
                _horizontalOffset = v;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Total content height.</summary>
    public double ExtentHeight => (RowCount * RowHeight) + (RowHeight * 2);

    /// <summary>Visible height.</summary>
    public double ViewportHeight => Bounds.Height;

    /// <summary>Total content width of the text area.</summary>
    public double ExtentWidth => WordWrap ? ViewportWidth : (_maxDisplayLength * CharWidth) + (TextPadding * 2) + (CharWidth * 4);

    /// <summary>Visible width of the text area.</summary>
    public double ViewportWidth => Math.Max(0, Bounds.Width - TextAreaLeft);

    /// <summary>Fractional line displayed at the top of the viewport.</summary>
    public double TopLine => RowToLine(_verticalOffset / RowHeight);

    /// <summary>Number of fully or partially visible lines.</summary>
    public int VisibleLineCount => (int)Math.Ceiling(ViewportHeight / RowHeight);

    /// <summary>True during the visible phase of the caret blink.</summary>
    public bool CaretVisible { get; private set; } = true;

    /// <summary>Scrolls so that the (fractional) <paramref name="line"/> is at the top.</summary>
    public void ScrollToLine(double line) => VerticalOffset = LineToRow(line) * RowHeight;

    /// <summary>Scrolls so that <paramref name="line"/> is roughly centred.</summary>
    public void CenterLine(int line) => VerticalOffset = (LineToRow(line) * RowHeight) - (ViewportHeight / 3);

    /// <summary>Returns the y coordinate (control space) of the top of <paramref name="line"/>; line may equal LineCount.</summary>
    public double GetLineTop(int line) => (LineToRow(line) * RowHeight) - _verticalOffset;

    /// <summary>Returns the fractional line at control y coordinate.</summary>
    public double GetLineAtY(double y) => RowToLine((y + _verticalOffset) / RowHeight);

    /// <summary>Length (chars) of a line.</summary>
    public int LineLength(int line) => Source is { } s && line >= 0 && line < s.LineCount ? s.GetLine(line).Length : 0;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is IEditorTextSource old)
            {
                old.Changed -= OnSourceChanged;
            }

            if (change.NewValue is IEditorTextSource source)
            {
                source.Changed += OnSourceChanged;
            }

            _caret = default;
            _anchor = default;
            _verticalOffset = 0;
            _horizontalOffset = 0;
            ResetTextCaches(true);
        }
        else if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            _typeface = new Typeface(FontFamily);
            _metricsValid = false;
            ResetTextCaches(false);
        }
        else if (change.Property == TabSizeProperty || change.Property == ShowWhitespaceProperty || change.Property == WordWrapProperty)
        {
            if (change.Property == WordWrapProperty)
            {
                _horizontalOffset = 0;
            }

            ResetTextCaches(change.Property == TabSizeProperty);
        }
        else if (change.Property == IsFocusedProperty)
        {
            if (IsFocused)
            {
                CaretVisible = true;
                _caretTimer.Start();
            }
            else
            {
                _caretTimer.Stop();
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            Size size = Bounds.Size;
            if (size != _lastSize)
            {
                bool widthChanged = Math.Abs(size.Width - _lastSize.Width) > 0.5;
                _lastSize = size;
                if (widthChanged && WordWrap)
                {
                    _rowPrefix = null;
                    _textCache.Clear();
                }

                VerticalOffset = _verticalOffset;
                HorizontalOffset = _horizontalOffset;
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void OnDecorationSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (IEditorDecorationSource s in e.OldItems)
            {
                s.Invalidated -= OnDecorationsInvalidated;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (IEditorDecorationSource s in e.NewItems)
            {
                s.Invalidated += OnDecorationsInvalidated;
            }
        }

        InvalidateVisual();
    }

    private void OnDecorationsInvalidated(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
        }
        else
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    private void OnSourceChanged(object? sender, TextSourceChangedEventArgs e)
    {
        if (e.IsReset)
        {
            ResetTextCaches(true);
            IEditorTextSource src = Source!;
            _caret = src.Clamp(_caret);
            _anchor = src.Clamp(_anchor);
            return;
        }

        _displayCache.Clear();
        _textCache.Clear();
        _rowPrefix = null;
        for (int i = e.StartLine; i < e.StartLine + e.InsertedLines && i < LineCount; i++)
        {
            _maxDisplayLength = Math.Max(_maxDisplayLength, GetDisplayLine(i).Text.Length);
        }

        VerticalOffset = _verticalOffset;
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateTheme()
    {
        _palette.Invalidate();
        _textCache.Clear();
        InvalidateVisual();
    }

    private void ResetTextCaches(bool rescanWidth)
    {
        _displayCache.Clear();
        _textCache.Clear();
        _rowPrefix = null;
        if (rescanWidth)
        {
            int max = 0;
            if (Source is { } s)
            {
                int tab = TabSize;
                for (int i = 0; i < s.LineCount; i++)
                {
                    max = Math.Max(max, DisplayLength(s.GetLine(i), tab));
                }
            }

            _maxDisplayLength = max;
        }

        VerticalOffset = _verticalOffset;
        HorizontalOffset = _horizontalOffset;
        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureMetrics()
    {
        if (_metricsValid)
        {
            return;
        }

        FormattedText probe = new(new string('M', 64), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.Black);
        _charWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace / 64);
        _rowHeight = Math.Ceiling(probe.Height + 3);
        _metricsValid = true;
    }

    private static int DigitCount(int n) => n < 10 ? 1 : (int)Math.Floor(Math.Log10(n)) + 1;

    private static int DisplayLength(string line, int tab)
    {
        if (!line.Contains('\t'))
        {
            return line.Length;
        }

        int col = 0;
        foreach (char c in line)
        {
            col = c == '\t' ? ((col / tab) + 1) * tab : col + 1;
        }

        return col;
    }

    /// <summary>The display form of a line: tabs expanded, optional whitespace glyphs, plus a source-to-display column map.</summary>
    internal sealed class DisplayLine(string text, int[]? map)
    {
        public string Text { get; } = text;

        public int[]? Map { get; } = map;

        public int ToDisplay(int column) => Map is null ? column : Map[Math.Clamp(column, 0, Map.Length - 1)];

        public int ToSource(int displayColumn)
        {
            if (Map is null)
            {
                return displayColumn;
            }

            for (int i = 0; i < Map.Length - 1; i++)
            {
                int mid = (Map[i] + Map[i + 1] + 1) / 2;
                if (displayColumn < mid)
                {
                    return i;
                }
            }

            return Map.Length - 1;
        }
    }

    internal DisplayLine GetDisplayLine(int line)
    {
        if (_displayCache.TryGetValue(line, out DisplayLine? d))
        {
            return d;
        }

        string src = Source!.GetLine(line);
        bool ws = ShowWhitespace;
        if (!src.Contains('\t') && !ws)
        {
            d = new DisplayLine(src, null);
        }
        else
        {
            int tab = TabSize;
            int[] map = new int[src.Length + 1];
            System.Text.StringBuilder sb = new(src.Length + 8);
            for (int i = 0; i < src.Length; i++)
            {
                map[i] = sb.Length;
                char c = src[i];
                if (c == '\t')
                {
                    int next = ((sb.Length / tab) + 1) * tab;
                    sb.Append(ws ? '→' : ' ');
                    sb.Append(' ', next - sb.Length);
                }
                else if (c == ' ' && ws)
                {
                    sb.Append('·');
                }
                else
                {
                    sb.Append(c);
                }
            }

            map[src.Length] = sb.Length;
            d = new DisplayLine(sb.ToString(), map);
        }

        if (_displayCache.Count > MaxCachedLayouts)
        {
            _displayCache.Clear();
        }

        _displayCache[line] = d;
        return d;
    }

    /// <summary>Returns the label layout of a phantom row.</summary>
    internal FormattedText GetPhantomText(PhantomRow phantom)
    {
        FormattedText ft = new(phantom.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(_typeface.FontFamily, FontStyle.Italic), FontSize * 0.92, _palette.MarkBrush(phantom.Kind));
        return ft;
    }

    /// <summary>Returns the cached text layout for a visual row.</summary>
    internal FormattedText? GetRowText(VisualRow row)
    {
        if (_textCache.TryGetValue((row.Line, row.Segment), out FormattedText? ft))
        {
            return ft;
        }

        DisplayLine d = GetDisplayLine(row.Line);
        int ds = d.ToDisplay(row.StartColumn);
        int de = d.ToDisplay(row.EndColumn);
        if (de <= ds)
        {
            return null;
        }

        string text = d.Text[ds..de];
        ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _palette.Foreground);
        if (ShowWhitespace)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '·' or '→')
                {
                    int s = i;
                    while (i + 1 < text.Length && text[i + 1] is '·' or '→' or ' ')
                    {
                        i++;
                    }

                    ft.SetForegroundBrush(_palette.Whitespace, s, i - s + 1);
                }
            }
        }

        if (_textCache.Count > MaxCachedLayouts)
        {
            _textCache.Clear();
        }

        _textCache[(row.Line, row.Segment)] = ft;
        return ft;
    }

    /// <summary>Number of realized (cached) row layouts; exposed for virtualization tests.</summary>
    public int CachedLayoutCount => _textCache.Count;

    /// <summary>X coordinate in control space of a source column on a row.</summary>
    public double ColumnToX(VisualRow row, int column)
    {
        DisplayLine d = GetDisplayLine(row.Line);
        int segStart = d.ToDisplay(row.StartColumn);
        return TextAreaLeft - _horizontalOffset + ((d.ToDisplay(column) - segStart) * CharWidth);
    }

    /// <summary>Collects decorations of all sources for a line range (also used by the overview ruler).</summary>
    public DecorationSink CollectDecorations(int firstLine, int lastLine)
    {
        DecorationSink sink = new();
        foreach (IEditorDecorationSource s in DecorationSources)
        {
            s.GetDecorations(firstLine, lastLine, sink);
        }

        return sink;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(_palette.Background, bounds);
        IReadOnlyList<VisualRow> rows = GetVisibleRows();
        _sink.Clear();
        if (rows.Count > 0 && LineCount > 0)
        {
            int first = rows[0].Line, last = Math.Min(rows[^1].Line, LineCount - 1);
            foreach (IEditorDecorationSource s in DecorationSources)
            {
                s.GetDecorations(first, last + 1, _sink);
            }
        }

        EditorRenderContext frame = new(this, _palette, _sink, rows);
        double gutter = GutterWidth;
        using (context.PushClip(new Rect(gutter, 0, Math.Max(0, bounds.Width - gutter), bounds.Height)))
        {
            foreach (IEditorLayer layer in Layers)
            {
                layer.Render(context, frame);
            }
        }

        RenderGutter(context, frame, gutter);
    }

    private void RenderGutter(DrawingContext context, EditorRenderContext frame, double gutter)
    {
        context.FillRectangle(_palette.GutterBackground, new Rect(0, 0, gutter, Bounds.Height));
        context.FillRectangle(_palette.GutterSeparator, new Rect(gutter - 1, 0, 1, Bounds.Height));
        double markerLeft = gutter - MarkerWidth - 1;

        Dictionary<int, HighlightKind> lineKinds = [];
        foreach (LineDecoration d in frame.Decorations.Lines)
        {
            lineKinds.TryAdd(d.Line, d.Kind);
        }

        foreach (VisualRow row in frame.Rows)
        {
            if (row.Phantom is { } phantom)
            {
                context.FillRectangle(_palette.MarkBrush(phantom.Kind), new Rect(markerLeft + 1, row.Top, 3, frame.RowHeight));
                continue;
            }

            if (lineKinds.TryGetValue(row.Line, out HighlightKind kind) && kind != HighlightKind.None)
            {
                context.FillRectangle(_palette.MarkBrush(kind), new Rect(markerLeft + 1, row.Top, 3, frame.RowHeight));
            }

            if (ShowLineNumbers && row.Segment == 0)
            {
                IBrush brush = row.Line == _caret.Line && IsFocused ? _palette.LineNumberCurrent : _palette.LineNumber;
                FormattedText num = new((row.Line + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize * 0.9, brush);
                context.DrawText(num, new Point(markerLeft - 4 - num.Width, row.Top + ((frame.RowHeight - num.Height) / 2)));
            }
        }

        foreach (GutterMark mark in frame.Decorations.Marks)
        {
            foreach (VisualRow row in frame.RowsOfLine(mark.Line))
            {
                if (row.Segment != 0)
                {
                    continue;
                }

                double size = Math.Min(MarkerWidth - 4, frame.RowHeight - 6);
                Point c = new(markerLeft + 7.5, row.Top + (frame.RowHeight / 2));
                context.DrawEllipse(_palette.MarkBrush(mark.Kind), null, c, size / 2, size / 2);
            }
        }
    }
}
