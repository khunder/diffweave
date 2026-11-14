using DiffWeave.App.Editor;
using DiffWeave.Core.Diff;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Documents;

/// <summary>Services the document needs from its host window (status messages, proposal UI).</summary>
public interface IDocumentHost
{
    /// <summary>Shows a status message.</summary>
    void ShowMessage(string message, MessageSeverity severity);

    /// <summary>Shows assistant proposals for review; returns the number accepted.</summary>
    Task<int> OfferProposalsAsync(MergeAssistantResult result, string sourceDisplayName, CancellationToken cancellationToken);
}

/// <summary>
/// Base class of the documents shown in the main window (two-/three-way comparison, merge). Owns the panes, the
/// line mapping between them and the current difference; implements <see cref="IEditorContext"/> for plugins.
/// </summary>
public abstract class DiffDocument : IEditorContext
{
    private int _currentBlock = -1;

    /// <summary>The panes in display order.</summary>
    public IReadOnlyList<PaneModel> Panes { get; protected set; } = [];

    /// <summary>The input files in pane order.</summary>
    public IReadOnlyList<InputFile> Inputs { get; protected set; } = [];

    /// <summary>The line mapping between panes; null until computed.</summary>
    public PaneMapping? Mapping { get; protected set; }

    /// <summary>The diff options.</summary>
    public DiffOptions Options { get; set; } = DiffOptions.Default;

    /// <summary>The host (status bar, proposal UI); optional.</summary>
    public IDocumentHost? Host { get; set; }

    /// <summary>The language id of the document.</summary>
    public string? LanguageId { get; set; }

    /// <inheritdoc />
    public abstract EditorKind Kind { get; }

    /// <summary>Raised after the mapping was (re)computed.</summary>
    public event EventHandler? MappingChanged;

    /// <summary>Raised when the current block changed.</summary>
    public event EventHandler? CurrentBlockChanged;

    /// <summary>Raised when counts or dirty state changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Index of the current difference block, or -1.</summary>
    public int CurrentBlock
    {
        get => _currentBlock;
        set
        {
            int v = Mapping is null ? -1 : Math.Clamp(value, -1, Mapping.Blocks.Count - 1);
            if (v != _currentBlock)
            {
                _currentBlock = v;
                CurrentBlockChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The current block or null.</summary>
    public DiffBlock? Current => Mapping is { } m && _currentBlock >= 0 && _currentBlock < m.Blocks.Count ? m.Blocks[_currentBlock] : null;

    /// <summary>Number of difference blocks (excluding whitespace-only ones when ignored).</summary>
    public int DifferenceCount => Mapping?.Blocks.Count(IsNavigable) ?? 0;

    /// <summary>Ordinal (1-based) of the current block among navigable blocks, or 0.</summary>
    public int CurrentOrdinal => Current is { } c && IsNavigable(c) ? Mapping!.Blocks.Take(c.Index + 1).Count(IsNavigable) : 0;

    /// <summary>True for blocks that count as differences (not hidden by ignore options).</summary>
    public static bool IsNavigable(DiffBlock b) => b.Kind != HighlightKind.None || b.IsConflict;

    /// <summary>The window title fragment.</summary>
    public virtual string Title => string.Join(" ↔ ", Inputs.Select(i => i.Label));

    /// <inheritdoc />
    public IReadOnlyList<string> PanePaths => [.. Inputs.Select(i => i.Path ?? i.Label)];

    /// <summary>Recomputes the diff (e.g. after options changed). Heavy work runs on the thread pool.</summary>
    public abstract Task RecomputeAsync(CancellationToken cancellationToken = default);

    /// <summary>Selects the next difference; returns false at the end.</summary>
    public bool GoToNextDifference(Func<DiffBlock, bool>? filter = null)
    {
        if (Mapping is null)
        {
            return false;
        }

        for (int i = _currentBlock + 1; i < Mapping.Blocks.Count; i++)
        {
            if (IsNavigable(Mapping.Blocks[i]) && (filter is null || filter(Mapping.Blocks[i])))
            {
                CurrentBlock = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>Selects the previous difference; returns false at the start.</summary>
    public bool GoToPreviousDifference(Func<DiffBlock, bool>? filter = null)
    {
        if (Mapping is null)
        {
            return false;
        }

        int start = _currentBlock < 0 ? Mapping.Blocks.Count - 1 : _currentBlock - 1;
        for (int i = start; i >= 0; i--)
        {
            if (IsNavigable(Mapping.Blocks[i]) && (filter is null || filter(Mapping.Blocks[i])))
            {
                CurrentBlock = i;
                return true;
            }
        }

        return false;
    }

    /// <summary>Selects the first difference.</summary>
    public bool GoToFirstDifference()
    {
        _currentBlock = -1;
        return GoToNextDifference();
    }

    /// <summary>Selects the last difference.</summary>
    public bool GoToLastDifference()
    {
        _currentBlock = -1;
        return GoToPreviousDifference();
    }

    /// <summary>Selects the block at a line of a pane (caret navigation).</summary>
    public void SelectBlockAt(int pane, int line)
    {
        if (Mapping?.FindBlockAtLine(pane, line) is { } b)
        {
            CurrentBlock = b.Index;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPaneLines(int pane)
    {
        IEditorTextSource s = Panes[pane].Source;
        string[] lines = new string[s.LineCount];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = s.GetLine(i);
        }

        return lines;
    }

    /// <inheritdoc />
    public virtual MergeSnapshot? GetMergeSnapshot() => null;

    /// <inheritdoc />
    public Task<int> OfferProposalsAsync(MergeAssistantResult result, string sourceDisplayName, CancellationToken cancellationToken)
        => Host?.OfferProposalsAsync(result, sourceDisplayName, cancellationToken) ?? Task.FromResult(0);

    /// <inheritdoc />
    public void ShowMessage(string message, MessageSeverity severity = MessageSeverity.Information) => Host?.ShowMessage(message, severity);

    /// <summary>Raises <see cref="MappingChanged"/>.</summary>
    protected void OnMappingChanged()
    {
        if (Mapping is null || _currentBlock >= Mapping.Blocks.Count)
        {
            _currentBlock = -1;
        }

        MappingChanged?.Invoke(this, EventArgs.Empty);
        CurrentBlockChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raises <see cref="StateChanged"/>.</summary>
    protected void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Sets header data of a pane from its input.</summary>
    protected static void DescribePane(PaneModel pane, InputFile input)
    {
        pane.Input = input;
        pane.Title = input.Label;
        pane.Path = input.Path;
        pane.Info = input.InfoText;
    }
}
