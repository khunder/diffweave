using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>
/// The overview ruler (minimap of differences): shows every difference block and conflict of the whole comparison,
/// scaled to the ruler height, with one lane per pane plus the visible area of the reference view. Clicking or
/// dragging jumps to that position.
/// </summary>
public sealed class OverviewRuler : Control
{
    private readonly EditorPalette _palette;
    private DiffTextView? _view;
    private bool _dragging;

    /// <summary>Creates the ruler.</summary>
    public OverviewRuler()
    {
        _palette = new EditorPalette(this);
        Width = 18;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => { _palette.Invalidate(); InvalidateVisual(); };
        Cursor = new Cursor(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, "Overview of differences");
    }

    /// <summary>Raised with the alignment row the user clicked.</summary>
    public event EventHandler<double>? RowRequested;

    /// <summary>The mapping providing blocks and row coordinates.</summary>
    public PaneMapping? Mapping
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The pane (index in the mapping) of the reference view.</summary>
    public int ReferencePane { get; set; }

    /// <summary>Which panes get a lane, in order.</summary>
    public IReadOnlyList<int> Lanes { get; set; } = [0, 1];

    /// <summary>Kind override per block and pane (e.g. resolved state).</summary>
    public Func<DiffBlock, int, HighlightKind>? KindSelector { get; set; }

    /// <summary>The reference view whose viewport is drawn.</summary>
    public void Attach(DiffTextView view)
    {
        if (_view is not null)
        {
            _view.ScrollChanged -= OnScroll;
        }

        _view = view;
        view.ScrollChanged += OnScroll;
        InvalidateVisual();
    }

    private void OnScroll(object? sender, EventArgs e) => InvalidateVisual();

    private double Scale => Mapping is { RowCount: > 0 } m ? Math.Max(0, Bounds.Height - 4) / m.RowCount : 0;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_palette.Get("DW.Ruler.Background"), new Rect(Bounds.Size));
        context.FillRectangle(_palette.Get("DW.Pane.Border"), new Rect(0, 0, 1, Bounds.Height));
        if (Mapping is not { } m || m.RowCount == 0)
        {
            return;
        }

        double scale = Scale;
        int lanes = Math.Max(1, Lanes.Count);
        double laneWidth = (Bounds.Width - 4) / lanes;
        foreach (DiffBlock b in m.Blocks)
        {
            double y = 2 + (b.StartRow * scale);
            double h = Math.Max(2, b.RowCount * scale);
            for (int i = 0; i < Lanes.Count; i++)
            {
                int pane = Lanes[i];
                HighlightKind kind = KindSelector?.Invoke(b, pane) ?? b.KindFor(pane);
                if (kind == HighlightKind.None)
                {
                    continue;
                }

                context.FillRectangle(_palette.MarkBrush(kind), new Rect(2 + (i * laneWidth) + 0.5, y, Math.Max(1, laneWidth - 1), h));
            }
        }

        if (_view is not null && _view.LineCount > 0)
        {
            double top = m.LineToRow(ReferencePane, _view.TopLine);
            double bottomLine = _view.GetLineAtY(_view.ViewportHeight);
            double bottom = m.LineToRow(ReferencePane, bottomLine);
            double y1 = 2 + (top * scale);
            double y2 = Math.Max(y1 + 6, 2 + (bottom * scale));
            Rect r = new(1, y1, Bounds.Width - 2, Math.Min(y2, Bounds.Height - 1) - y1);
            context.DrawRectangle(_palette.Get("DW.Ruler.Viewport"), new Pen(_palette.Get("DW.Ruler.ViewportBorder"), 1), r, 2, 2);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Request(e.GetPosition(this).Y);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Request(e.GetPosition(this).Y);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Request(double y)
    {
        double scale = Scale;
        if (scale <= 0)
        {
            return;
        }

        RowRequested?.Invoke(this, Math.Clamp((y - 2) / scale, 0, Mapping!.RowCount));
    }
}
