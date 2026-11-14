using DiffWeave.App.Editor;

namespace DiffWeave.App.Controls;

/// <summary>
/// Keeps the vertical and horizontal scroll positions of several <see cref="DiffTextView"/>s in sync. Vertical sync
/// maps the line at the middle of the scrolled view through the <see cref="PaneMapping"/> to the other panes, so that
/// corresponding lines stay side by side even though panes do not contain filler lines.
/// </summary>
public sealed class ScrollSynchronizer
{
    private readonly List<(DiffTextView View, int Pane)> _views = [];
    private bool _syncing;

    /// <summary>The line mapping; null disables vertical mapping (offsets are copied 1:1).</summary>
    public PaneMapping? Mapping { get; set; }

    /// <summary>When false, scrolling is not synchronized.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The registered views.</summary>
    public IReadOnlyList<(DiffTextView View, int Pane)> Views => _views;

    /// <summary>Adds a view showing pane <paramref name="pane"/> of the mapping.</summary>
    public void Add(DiffTextView view, int pane)
    {
        _views.Add((view, pane));
        view.ScrollChanged += OnScrollChanged;
    }

    /// <summary>Removes all views.</summary>
    public void Clear()
    {
        foreach ((DiffTextView v, _) in _views)
        {
            v.ScrollChanged -= OnScrollChanged;
        }

        _views.Clear();
    }

    /// <summary>Synchronizes the others to <paramref name="source"/>.</summary>
    public void SyncFrom(DiffTextView source)
    {
        if (_syncing || !IsEnabled)
        {
            return;
        }

        int index = _views.FindIndex(v => ReferenceEquals(v.View, source));
        if (index < 0)
        {
            return;
        }

        _syncing = true;
        try
        {
            int pane = _views[index].Pane;
            double half = source.ViewportHeight / 2;
            double midLine = source.GetLineAtY(half);
            foreach ((DiffTextView view, int otherPane) in _views)
            {
                if (ReferenceEquals(view, source))
                {
                    continue;
                }

                view.HorizontalOffset = source.HorizontalOffset;
                if (Mapping is null)
                {
                    view.VerticalOffset = source.VerticalOffset;
                    continue;
                }

                double mapped = Mapping.MapLine(pane, otherPane, midLine);
                double y = (view.LineToRow(mapped) * view.RowHeight) - (view.ViewportHeight / 2);
                if (source.VerticalOffset <= 0.5)
                {
                    y = Math.Min(y, 0);
                }

                view.VerticalOffset = y;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnScrollChanged(object? sender, EventArgs e)
    {
        if (sender is DiffTextView v)
        {
            SyncFrom(v);
        }
    }
}
