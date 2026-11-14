using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DiffWeave.App.Controls;
using DiffWeave.App.Documents;
using DiffWeave.App.Editor;
using DiffWeave.App.Services;

namespace DiffWeave.App.Views;

/// <summary>
/// Lays out the panes of a <see cref="DiffDocument"/> KDiff3-style: A | B (two-way), A | B | C (three-way) and, for
/// merges, the editable OUTPUT pane below the inputs. Adds connecting bands between adjacent panes, the overview
/// ruler, synchronized scrolling and current-difference highlighting.
/// </summary>
public sealed class DiffDocumentView : Grid
{
    private readonly List<EditorPane> _panes = [];
    private readonly List<DiffBand> _bands = [];
    private readonly ScrollSynchronizer _sync = new();
    private readonly OverviewRuler _ruler = new();
    private EditorPane? _reference;

    /// <summary>Creates the view for a document.</summary>
    public DiffDocumentView(DiffDocument document)
    {
        Document = document;
        BuildLayout();
        document.MappingChanged += (_, _) => OnMappingChanged();
        document.CurrentBlockChanged += (_, _) => OnCurrentBlockChanged(true);
        OnMappingChanged();
    }

    /// <summary>Raised when the user asks to open another file in a pane (argument: pane index).</summary>
    public event EventHandler<int>? OpenPaneRequested;

    /// <summary>The document.</summary>
    public DiffDocument Document { get; }

    /// <summary>The panes in pane-index order of the document (hidden panes excluded).</summary>
    public IReadOnlyList<EditorPane> Panes => _panes;

    /// <summary>The bands.</summary>
    public IReadOnlyList<DiffBand> Bands => _bands;

    /// <summary>The overview ruler.</summary>
    public OverviewRuler Ruler => _ruler;

    /// <summary>The scroll synchronizer.</summary>
    public ScrollSynchronizer Synchronizer => _sync;

    /// <summary>The pane whose text view had focus last.</summary>
    public EditorPane? ActivePane { get; private set; }

    /// <summary>The editable output pane of a merge, or null.</summary>
    public EditorPane? OutputPane { get; private set; }

    /// <summary>Returns the editor pane showing document pane <paramref name="index"/>.</summary>
    public EditorPane? GetPane(int index) => _panes.FirstOrDefault(p => p.Tag is PaneModel m && m.Index == index);

    /// <summary>Applies appearance settings to all panes.</summary>
    public void ApplySettings(AppSettings s)
    {
        FontFamily family;
        try
        {
            family = FontFamily.Parse(string.IsNullOrWhiteSpace(s.FontFamily) ? DiffTextView.DefaultFontFamily : s.FontFamily);
        }
        catch (ArgumentException)
        {
            family = FontFamily.Parse(DiffTextView.DefaultFontFamily);
        }

        foreach (EditorPane p in _panes)
        {
            p.View.FontFamily = family;
            p.View.FontSize = s.FontSize;
            p.View.TabSize = s.TabSize;
            p.View.ShowWhitespace = s.ShowWhitespace;
            p.View.ShowLineNumbers = s.ShowLineNumbers;
            p.View.WordWrap = s.WordWrap;
        }
    }

    /// <summary>Scrolls the current block into view in all panes.</summary>
    public void RevealCurrent() => OnCurrentBlockChanged(true);

    private void BuildLayout()
    {
        IReadOnlyList<PaneModel> models = Document.Panes;
        List<PaneModel> top;
        PaneModel? output = null;
        if (Document is MergeDocument merge)
        {
            top = [.. models.Where(m => m.Index != MergeDocument.OutputPane && (merge.HasBase || m.Index != 0))];
            output = merge.OutputPaneModel;
        }
        else
        {
            top = [.. models];
        }

        // Columns: pane, band, pane, [band, pane], ruler
        for (int i = 0; i < top.Count; i++)
        {
            if (i > 0)
            {
                ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            }

            ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 120 });
        }

        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        int columnCount = ColumnDefinitions.Count;
        RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 80 });
        if (output is not null)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 80 });
        }

        EditorPane? previous = null;
        PaneModel? previousModel = null;
        for (int i = 0; i < top.Count; i++)
        {
            EditorPane pane = CreatePane(top[i]);
            SetColumn(pane, i * 2);
            Children.Add(pane);
            if (previous is not null)
            {
                DiffBand band = new() { LeftPane = previousModel!.Index, RightPane = top[i].Index };
                band.Attach(previous.View, pane.View);
                band.BlockClicked += (_, b) => Document.CurrentBlock = b.Index;
                band.Margin = new Thickness(0, HeaderOffset, 0, 0);
                SetColumn(band, (i * 2) - 1);
                Children.Add(band);
                _bands.Add(band);
            }

            previous = pane;
            previousModel = top[i];
        }

        if (output is not null)
        {
            GridSplitter splitter = new() { Height = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
            SetRow(splitter, 1);
            SetColumnSpan(splitter, columnCount - 1);
            Children.Add(splitter);
            EditorPane outPane = CreatePane(output);
            outPane.View.IsReadOnly = false;
            SetRow(outPane, 2);
            SetColumnSpan(outPane, columnCount - 1);
            Children.Add(outPane);
            OutputPane = outPane;
        }

        _reference = OutputPane ?? (_panes.Count > 1 ? _panes[1] : _panes[0]);
        _ruler.ReferencePane = ((PaneModel)_reference.Tag!).Index;
        _ruler.Lanes = [.. _panes.Select(p => ((PaneModel)p.Tag!).Index)];
        _ruler.Width = 6 + (_panes.Count * 5);
        _ruler.Attach(_reference.View);
        _ruler.RowRequested += (_, row) =>
        {
            if (Document.Mapping is { } m && _reference is not null)
            {
                double line = m.RowToLine(((PaneModel)_reference.Tag!).Index, row);
                _reference.View.VerticalOffset = (_reference.View.LineToRow(line) * _reference.View.RowHeight) - (_reference.View.ViewportHeight / 2);
            }
        };
        _ruler.Margin = new Thickness(0, 0, 0, 0);
        SetColumn(_ruler, columnCount - 1);
        SetRowSpan(_ruler, RowDefinitions.Count);
        Children.Add(_ruler);
        ActivePane = OutputPane ?? _panes[0];
        _reference.View.SizeChanged += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0 && _reference is not null)
        {
            _reference.View.SizeChanged -= OnFirstLayout;
            OnCurrentBlockChanged(true);
        }
    }

    private const double HeaderOffset = 0;

    private EditorPane CreatePane(PaneModel model)
    {
        EditorPane pane = new() { Tag = model };
        pane.View.Source = model.Source;
        if (model.DiffDecorations is not null)
        {
            pane.View.DecorationSources.Add(model.DiffDecorations);
        }

        pane.View.DecorationSources.Add(model.PluginDecorations);
        pane.SetHeader(model.Badge, model.BadgeResourceKey, model.Title, model.Path, model.Info);
        pane.OpenButton.IsVisible = model.Role != Extensibility.PaneRole.Output;
        pane.OpenRequested += (_, _) => OpenPaneRequested?.Invoke(this, model.Index);
        pane.View.GotFocus += (_, _) => ActivePane = pane;
        pane.View.PointerReleased += (_, _) => SelectBlockAtCaret(pane, model);
        pane.View.KeyUp += (_, _) => SelectBlockAtCaret(pane, model);
        _sync.Add(pane.View, model.Index);
        _panes.Add(pane);
        return pane;
    }

    private void SelectBlockAtCaret(EditorPane pane, PaneModel model)
    {
        if (Document.Mapping is null)
        {
            return;
        }

        int line = pane.View.CaretPosition.Line;
        if (Document.Mapping.FindBlockAtLine(model.Index, line) is { } b && b.Index != Document.CurrentBlock)
        {
            Document.CurrentBlock = b.Index;
            OnCurrentBlockChanged(false);
        }
    }

    private void OnMappingChanged()
    {
        PaneMapping? m = Document.Mapping;
        _sync.Mapping = m;
        _ruler.Mapping = m;
        foreach (DiffBand b in _bands)
        {
            b.Mapping = m;
        }

        foreach (EditorPane p in _panes)
        {
            PaneModel model = (PaneModel)p.Tag!;
            p.View.SetPhantomRows(model.PhantomRows);
            p.SetHeader(model.Badge, model.BadgeResourceKey, model.Title, model.Path, model.Info);
            p.View.InvalidateVisual();
        }

        OnCurrentBlockChanged(false);
    }

    private void OnCurrentBlockChanged(bool scroll)
    {
        DiffBlock? block = Document.Current;
        foreach (EditorPane p in _panes)
        {
            PaneModel model = (PaneModel)p.Tag!;
            p.View.CurrentRegion = block?.Spans[model.Index];
        }

        foreach (DiffBand b in _bands)
        {
            b.CurrentBlock = block?.Index ?? -1;
        }

        _ruler.InvalidateVisual();
        if (scroll && block is not null)
        {
            EditorPane target = ActivePane ?? _reference!;
            int pane = ((PaneModel)target.Tag!).Index;
            LineSpan span = block.Spans[pane];
            DiffTextView v = target.View;
            (double top, double bottom) = v.GetSpanBounds(span);
            double visibleTop = 0, visibleBottom = v.ViewportHeight;
            if (top < visibleTop + v.RowHeight || bottom > visibleBottom - v.RowHeight)
            {
                v.VerticalOffset += top - (v.ViewportHeight / 3);
            }
            else
            {
                _sync.SyncFrom(v);
            }

            if (span.Count > 0 && v.LineCount > 0)
            {
                v.SetCaret(new TextPos(Math.Min(span.Start, v.LineCount - 1), 0));
            }
        }
    }
}
