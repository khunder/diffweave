using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>
/// The connector between two adjacent panes: draws a curved ribbon from each difference block's lines in the left
/// pane to the corresponding lines in the right pane (Meld/KDiff3 style). Clicking a ribbon selects that block.
/// </summary>
public sealed class DiffBand : Control
{
    private readonly EditorPalette _palette;
    private DiffTextView? _left;
    private DiffTextView? _right;

    /// <summary>Creates the band.</summary>
    public DiffBand()
    {
        _palette = new EditorPalette(this);
        Width = 36;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => { _palette.Invalidate(); InvalidateVisual(); };
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Raised when the user clicks a block's ribbon.</summary>
    public event EventHandler<DiffBlock>? BlockClicked;

    /// <summary>Pane index (in <see cref="Mapping"/>) of the left view.</summary>
    public int LeftPane { get; set; }

    /// <summary>Pane index (in <see cref="Mapping"/>) of the right view.</summary>
    public int RightPane { get; set; }

    /// <summary>The mapping providing the blocks.</summary>
    public PaneMapping? Mapping
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>Index of the highlighted (current) block, or -1.</summary>
    public int CurrentBlock
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    } = -1;

    /// <summary>Kind override per block (e.g. resolved state), or null to use the block kind.</summary>
    public Func<DiffBlock, HighlightKind>? KindSelector { get; set; }

    /// <summary>Attaches the two views whose scroll positions define the ribbon ends.</summary>
    public void Attach(DiffTextView left, DiffTextView right)
    {
        if (_left is not null)
        {
            _left.ScrollChanged -= OnScroll;
        }

        if (_right is not null)
        {
            _right.ScrollChanged -= OnScroll;
        }

        _left = left;
        _right = right;
        left.ScrollChanged += OnScroll;
        right.ScrollChanged += OnScroll;
        InvalidateVisual();
    }

    private void OnScroll(object? sender, EventArgs e) => InvalidateVisual();

    private IEnumerable<(DiffBlock Block, double LTop, double LBottom, double RTop, double RBottom)> VisibleBlocks()
    {
        if (Mapping is null || _left is null || _right is null)
        {
            yield break;
        }

        double h = Bounds.Height;
        double offset = _left.TranslatePoint(default, this)?.Y ?? 0;
        double rOffset = _right.TranslatePoint(default, this)?.Y ?? 0;
        foreach (DiffBlock b in Mapping.Blocks)
        {
            (double lt, double lb) = _left.GetSpanBounds(b.Spans[LeftPane]);
            (double rt, double rb) = _right.GetSpanBounds(b.Spans[RightPane]);
            lt += offset;
            lb += offset;
            rt += rOffset;
            rb += rOffset;
            if ((lb < 0 && rb < 0) || (lt > h && rt > h))
            {
                if (lt > h && rt > h)
                {
                    yield break;
                }

                continue;
            }

            if ((b.Spans[LeftPane].Count == 0 && b.Spans[RightPane].Count == 0) || (b.Kind == HighlightKind.None && !b.IsConflict))
            {
                continue;
            }

            yield return (b, lt, lb, rt, rb);
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        context.FillRectangle(_palette.Get("DW.Band.Background"), new Rect(Bounds.Size));
        double clipTop = 0, clipBottom = Bounds.Height;
        if (_left is not null && _right is not null)
        {
            double lt = _left.TranslatePoint(default, this)?.Y ?? 0;
            double rt = _right.TranslatePoint(default, this)?.Y ?? 0;
            clipTop = Math.Max(lt, rt);
            clipBottom = Math.Min(lt + _left.Bounds.Height, rt + _right.Bounds.Height);
            context.FillRectangle(_palette.Get("DW.Pane.Border"), new Rect(0, clipTop - 1, w, 1));
        }

        using DrawingContext.PushedState clip = context.PushClip(new Rect(0, clipTop, w, Math.Max(0, clipBottom - clipTop)));
        foreach ((DiffBlock b, double lt, double lb, double rt, double rb) in VisibleBlocks())
        {
            HighlightKind kind = KindSelector?.Invoke(b) ?? b.Kind;
            StreamGeometry g = new();
            using (StreamGeometryContext ctx = g.Open())
            {
                double mid = w / 2;
                ctx.BeginFigure(new Point(0, lt), true);
                ctx.CubicBezierTo(new Point(mid, lt), new Point(mid, rt), new Point(w, rt));
                ctx.LineTo(new Point(w, Math.Max(rb, rt + 1)));
                ctx.CubicBezierTo(new Point(mid, Math.Max(rb, rt + 1)), new Point(mid, Math.Max(lb, lt + 1)), new Point(0, Math.Max(lb, lt + 1)));
                ctx.EndFigure(true);
            }

            bool current = b.Index == CurrentBlock;
            IPen pen = new Pen(current ? _palette.CurrentRegion : _palette.MarkBrush(kind), current ? 1.5 : 1);
            context.DrawGeometry(_palette.InlineBrush(kind), pen, g);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point p = e.GetPosition(this);
        foreach ((DiffBlock b, double lt, double lb, double rt, double rb) in VisibleBlocks())
        {
            double t = Bounds.Width <= 0 ? 0 : p.X / Bounds.Width;
            double top = lt + ((rt - lt) * t) - 3;
            double bottom = lb + ((rb - lb) * t) + 3;
            if (p.Y >= top && p.Y <= bottom)
            {
                BlockClicked?.Invoke(this, b);
                e.Handled = true;
                return;
            }
        }
    }
}
