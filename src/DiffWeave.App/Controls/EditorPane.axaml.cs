using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace DiffWeave.App.Controls;

/// <summary>A diff pane: header (badge, label, path, encoding/EOL/line count, open button), text view and scroll bars.</summary>
public partial class EditorPane : UserControl
{
    private bool _updatingScrollBars;

    /// <summary>Creates the pane.</summary>
    public EditorPane()
    {
        InitializeComponent();
        View.ScrollChanged += (_, _) => UpdateScrollBars();
        VScroll.Scroll += (_, e) =>
        {
            if (!_updatingScrollBars)
            {
                View.VerticalOffset = e.NewValue;
            }
        };
        HScroll.Scroll += (_, e) =>
        {
            if (!_updatingScrollBars)
            {
                View.HorizontalOffset = e.NewValue;
            }
        };
        VScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingScrollBars)
            {
                View.VerticalOffset = VScroll.Value;
            }
        };
        HScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updatingScrollBars)
            {
                View.HorizontalOffset = HScroll.Value;
            }
        };
        OpenButton.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        View.GotFocus += (_, _) => SetActive(true);
        View.LostFocus += (_, _) => SetActive(false);
    }

    /// <summary>Raised when the user clicks the open button.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>The text view.</summary>
    public DiffTextView View => PartView;

    /// <summary>The vertical scroll bar.</summary>
    public ScrollBar VScroll => PartVScroll;

    /// <summary>The horizontal scroll bar.</summary>
    public ScrollBar HScroll => PartHScroll;

    /// <summary>The open button.</summary>
    public Button OpenButton => PartOpenButton;

    /// <summary>Sets the header contents.</summary>
    public void SetHeader(string badge, string badgeResourceKey, string title, string? path, string info)
    {
        PartBadgeText.Text = badge;
        Border b = PartBadge;
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable(badgeResourceKey));
        PartTitleText.Text = title;
        TextBlock p = PartPathText;
        p.Text = path ?? string.Empty;
        p.IsVisible = !string.IsNullOrEmpty(path) && path != title;
        ToolTip.SetTip(p, path);
        PartInfoText.Text = info;
        AutomationProperties.SetName(View, $"{badge}: {title}");
    }

    /// <summary>Header info text (encoding, EOL, line count).</summary>
    public string InfoText => PartInfoText.Text ?? string.Empty;

    /// <summary>Header title.</summary>
    public string TitleText => PartTitleText.Text ?? string.Empty;

    private void SetActive(bool active)
    {
        Border header = PartHeader;
        header.BorderThickness = active ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 1);
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable(active ? "DW.Pane.ActiveAccent" : "DW.Pane.Border"));
    }

    private void UpdateScrollBars()
    {
        _updatingScrollBars = true;
        try
        {
            double vMax = Math.Max(0, View.ExtentHeight - View.ViewportHeight);
            VScroll.Maximum = vMax;
            VScroll.ViewportSize = View.ViewportHeight;
            VScroll.LargeChange = Math.Max(1, View.ViewportHeight - View.RowHeight);
            VScroll.SmallChange = View.RowHeight;
            VScroll.Value = View.VerticalOffset;
            double hMax = Math.Max(0, View.ExtentWidth - View.ViewportWidth);
            HScroll.Maximum = hMax;
            HScroll.ViewportSize = View.ViewportWidth;
            HScroll.LargeChange = Math.Max(1, View.ViewportWidth - 40);
            HScroll.SmallChange = View.CharWidth * 4;
            HScroll.Value = View.HorizontalOffset;
            HScroll.IsVisible = hMax > 0.5 && !View.WordWrap;
        }
        finally
        {
            _updatingScrollBars = false;
        }
    }
}
