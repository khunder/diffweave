using System.ComponentModel;
using Avalonia.Controls;
using DiffWeave.App.Editor;
using DiffWeave.App.ViewModels;

namespace DiffWeave.App.Views;

/// <summary>The Assistants side panel (hosts the proposal review UI).</summary>
public partial class AssistantsPanel : UserControl
{
    private readonly StaticDecorationSource _previewDecorations = new();
    private AssistantsViewModel? _vm;

    /// <summary>Creates the panel.</summary>
    public AssistantsPanel()
    {
        InitializeComponent();
        PreviewView.DecorationSources.Add(_previewDecorations);
        CloseButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        DocsButton.Click += (_, _) => DocsRequested?.Invoke(this, EventArgs.Empty);
        DataContextChanged += (_, _) => Attach(DataContext as AssistantsViewModel);
    }

    /// <summary>Raised by the close button.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised by the documentation link.</summary>
    public event EventHandler? DocsRequested;

    /// <summary>The preview editor.</summary>
    public Controls.DiffTextView Preview => PreviewView;

    private void Attach(AssistantsViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnVmChanged;
        }

        UpdatePreview();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantsViewModel.SelectedProposal))
        {
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        _previewDecorations.Clear();
        ProposalItem? p = _vm?.SelectedProposal;
        if (p is null)
        {
            PreviewView.Source = ReadOnlyTextSource.Empty;
            return;
        }

        for (int i = 0; i < p.PreviewKinds.Count; i++)
        {
            if (p.PreviewKinds[i] != HighlightKind.None)
            {
                _previewDecorations.Add(new LineDecoration(i, p.PreviewKinds[i]));
            }
        }

        _previewDecorations.NotifyChanged();
        PreviewView.Source = new ReadOnlyTextSource(p.PreviewLines);
    }
}
