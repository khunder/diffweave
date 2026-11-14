using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DiffWeave.App.Documents;
using DiffWeave.App.Services;

namespace DiffWeave.App.Views;

/// <summary>Directory comparison: filterable tree of entries with status, size and modification time per side.</summary>
public partial class DirectoryView : UserControl
{
    /// <summary>Required by the XAML loader; use the document constructor.</summary>
    public DirectoryView()
        : this(new DirectoryDocument(["."]), null)
    {
    }

    /// <summary>Creates the view.</summary>
    public DirectoryView(DirectoryDocument document, IDialogService? dialogs)
    {
        Document = document;
        Dialogs = dialogs;
        InitializeComponent();
        List.ItemsSource = document.Rows;
        bool three = document.Roots.Count > 2;
        HeadSizeC.IsVisible = three;
        HeadModC.IsVisible = three;
        string[] letters = ["A", "B", "C"];
        for (int i = 0; i < document.Roots.Count; i++)
        {
            RootsPanel.Children.Add(new TextBlock
            {
                Text = $"{letters[i]}:  {document.Labels[i]}",
                FontSize = 11.5,
                [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DW.Subtle"),
            });
        }

        ShowDifferent.IsCheckedChanged += (_, _) => UpdateFilter();
        ShowOnly.IsCheckedChanged += (_, _) => UpdateFilter();
        ShowIdentical.IsCheckedChanged += (_, _) => UpdateFilter();
        ExpandAllButton.Click += (_, _) => document.ExpandAll();
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        CopyRightButton.Click += async (_, _) => await CopySelectedAsync(0, 1);
        CopyLeftButton.Click += async (_, _) => await CopySelectedAsync(1, 0);
        List.DoubleTapped += (_, e) => ActivateSelected(e);
        List.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        document.Rows.CollectionChanged += (_, _) => EmptyText.IsVisible = document.Rows.Count == 0;
        ActualThemeVariantChanged += (_, _) => List.ItemsSource = null;
        ActualThemeVariantChanged += (_, _) => List.ItemsSource = document.Rows;
    }

    /// <summary>Raised when a file row is activated (double click / Enter) with its paths per side.</summary>
    public event EventHandler<DirectoryRow>? OpenRequested;

    /// <summary>Raised with a status message.</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>The document.</summary>
    public DirectoryDocument Document { get; }

    /// <summary>Dialogs used for confirmation.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>The selected row.</summary>
    public DirectoryRow? SelectedRow => List.SelectedItem as DirectoryRow;

    /// <summary>The list control (tests).</summary>
    public ListBox RowList => List;

    /// <summary>Compares again.</summary>
    public async Task RefreshAsync()
    {
        EmptyText.Text = "Comparing…";
        await Document.CompareAsync();
        EmptyText.Text = "No entries match the filter.";
        EmptyText.IsVisible = Document.Rows.Count == 0;
        StatusMessage?.Invoke(this, $"Compared {Document.Roots.Count} directories.");
    }

    /// <summary>Activates a row: toggles directories, opens files.</summary>
    public void Activate(DirectoryRow row)
    {
        if (row.IsDirectory)
        {
            Document.Toggle(row);
            SelectNode(row.Node.RelativePath);
        }
        else
        {
            OpenRequested?.Invoke(this, row);
        }
    }

    /// <summary>Copies the selected entry from one side to another after confirmation.</summary>
    public async Task<bool> CopySelectedAsync(int from, int to)
    {
        if (SelectedRow is not { } row || to >= Document.Roots.Count)
        {
            return false;
        }

        string?[] paths = [.. Document.PathsOf(row.Node)];
        if (paths[from] is null)
        {
            StatusMessage?.Invoke(this, $"'{row.Name}' does not exist on side {(char)('A' + from)}.");
            return false;
        }

        string letterFrom = ((char)('A' + from)).ToString(), letterTo = ((char)('A' + to)).ToString();
        string verb = paths[to] is null ? "Copy" : "Overwrite";
        if (Dialogs is not null)
        {
            int answer = await Dialogs.AskAsync(
                $"{verb} {letterTo}?",
                $"{verb} '{row.Node.RelativePath}' in {letterTo} with the version from {letterFrom}?\n\n{Document.Roots[to]}",
                verb,
                "Cancel");
            if (answer != 0)
            {
                return false;
            }
        }

        try
        {
            Document.Copy(row.Node, from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke(this, $"Copy failed: {ex.Message}");
            return false;
        }

        string rel = row.Node.RelativePath;
        await RefreshAsync();
        SelectNode(rel);
        StatusMessage?.Invoke(this, $"Copied '{rel}' from {letterFrom} to {letterTo}.");
        return true;
    }

    private void SelectNode(string relativePath)
    {
        List.SelectedItem = Document.Rows.FirstOrDefault(r => r.Node.RelativePath == relativePath);
    }

    private void UpdateFilter()
        => Document.Filter = new DirectoryFilter(ShowIdentical.IsChecked == true, ShowDifferent.IsChecked == true, ShowOnly.IsChecked == true);

    private void ActivateSelected(RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            Activate(row);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                Activate(row);
                e.Handled = true;
                break;
            case Key.Right when row.IsDirectory && !row.IsExpanded:
            case Key.Left when row.IsDirectory && row.IsExpanded:
                Document.Toggle(row);
                SelectNode(row.Node.RelativePath);
                e.Handled = true;
                break;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is TextBlock { Text: "▸" or "▾" } tb && tb.DataContext is DirectoryRow row)
        {
            Document.Toggle(row);
            SelectNode(row.Node.RelativePath);
            e.Handled = true;
        }
    }
}
