using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffWeave.App.Documents;
using DiffWeave.App.Views;

namespace DiffWeave.App.ViewModels;

/// <summary>A tab of the main window: a file comparison, a merge, a directory comparison or the start page.</summary>
public sealed partial class DocumentTab : ObservableObject
{
    /// <summary>Creates a tab.</summary>
    public DocumentTab(string title, Control content)
    {
        Title = title;
        Content = content;
    }

    /// <summary>Tab title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string Title { get; set; }

    /// <summary>True when the tab has unsaved changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial bool IsDirty { get; set; }

    /// <summary>Header text with a dirty marker.</summary>
    public string Header => IsDirty ? Title + " •" : Title;

    /// <summary>The tab content.</summary>
    public Control Content { get; }

    /// <summary>The diff/merge document, if any.</summary>
    public DiffDocument? Diff => (Content as DiffDocumentView)?.Document;

    /// <summary>The diff view, if any.</summary>
    public DiffDocumentView? DiffView => Content as DiffDocumentView;

    /// <summary>The directory view, if any.</summary>
    public DirectoryView? DirectoryView => Content as DirectoryView;

    /// <summary>True for the start page.</summary>
    public bool IsStartPage => Content is StartPage;
}
