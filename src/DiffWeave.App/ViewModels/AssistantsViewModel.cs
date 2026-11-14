using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffWeave.App.Documents;
using DiffWeave.App.Editor;
using DiffWeave.App.Services;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Plugins;
using DiffWeave.Extensibility;

namespace DiffWeave.App.ViewModels;

/// <summary>A registered merge assistant as listed in the panel.</summary>
public sealed class AssistantItem(IMergeAssistant assistant)
{
    /// <summary>The assistant.</summary>
    public IMergeAssistant Assistant { get; } = assistant;

    /// <summary>Display name.</summary>
    public string Name => Assistant.DisplayName;

    /// <summary>Description.</summary>
    public string Description => Assistant.Description ?? string.Empty;

    /// <summary>Human-readable capabilities.</summary>
    public string CapabilitiesText
    {
        get
        {
            List<string> parts = [];
            MergeAssistantCapabilities c = Assistant.Capabilities;
            if (c.HasFlag(MergeAssistantCapabilities.ProposeResolutions))
            {
                parts.Add("proposes resolutions");
            }

            if (c.HasFlag(MergeAssistantCapabilities.ExplainConflicts))
            {
                parts.Add("explains conflicts");
            }

            if (c.HasFlag(MergeAssistantCapabilities.Deterministic))
            {
                parts.Add("deterministic");
            }

            if (c.HasFlag(MergeAssistantCapabilities.UsesNetwork))
            {
                parts.Add("uses network");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Review state of a proposal.</summary>
public enum ProposalState
{
    /// <summary>Not decided yet.</summary>
    Pending,

    /// <summary>Applied to the merge.</summary>
    Accepted,

    /// <summary>Dismissed.</summary>
    Rejected,
}

/// <summary>A proposal shown for review.</summary>
public sealed partial class ProposalItem : ObservableObject
{
    internal ProposalItem(ResolutionProposal proposal, string source, string regionLabel, IReadOnlyList<string> previewLines, IReadOnlyList<HighlightKind> previewKinds)
    {
        Proposal = proposal;
        Source = source;
        RegionLabel = regionLabel;
        PreviewLines = previewLines;
        PreviewKinds = previewKinds;
    }

    /// <summary>The proposal.</summary>
    public ResolutionProposal Proposal { get; }

    /// <summary>Assistant display name.</summary>
    public string Source { get; }

    /// <summary>"Conflict 2 · output line 14".</summary>
    public string RegionLabel { get; }

    /// <summary>Rationale.</summary>
    public string Rationale => Proposal.Rationale;

    /// <summary>Confidence 0..1.</summary>
    public double Confidence => Math.Clamp(Proposal.Confidence, 0, 1);

    /// <summary>Confidence as percent text.</summary>
    public string ConfidenceText => Confidence.ToString("P0", CultureInfo.InvariantCulture);

    /// <summary>Unified preview lines (prefixed "-", "+", " ").</summary>
    public IReadOnlyList<string> PreviewLines { get; }

    /// <summary>Highlight of each preview line.</summary>
    public IReadOnlyList<HighlightKind> PreviewKinds { get; }

    /// <summary>Review state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(StateText))]
    public partial ProposalState State { get; set; }

    /// <summary>True while undecided.</summary>
    public bool IsPending => State == ProposalState.Pending;

    /// <summary>State label.</summary>
    public string StateText => State switch
    {
        ProposalState.Accepted => "Accepted",
        ProposalState.Rejected => "Rejected",
        _ => string.Empty,
    };
}

/// <summary>
/// The Assistants side panel: lists registered <see cref="IMergeAssistant"/>s, runs one for the current conflict
/// (or all unresolved conflicts), and lets the user preview, accept or reject the returned proposals. Accepted
/// proposals are applied through <see cref="Core.Merge.MergeSession.ApplyProposals"/> and are therefore undoable.
/// </summary>
public sealed partial class AssistantsViewModel : ObservableObject
{
    private readonly PluginService _plugins;
    private readonly Func<DiffDocument?> _document;
    private TaskCompletionSource<int>? _offer;
    private int _offerAccepted;
    private CancellationTokenSource? _runCts;

    /// <summary>The documentation pointer shown in the empty state.</summary>
    public const string DocsHint = "docs/extensibility.md";

    /// <summary>Creates the view model.</summary>
    public AssistantsViewModel(PluginService plugins, Func<DiffDocument?> document)
    {
        _plugins = plugins;
        _document = document;
        plugins.Changed += (_, _) => RefreshAssistants();
        RefreshAssistants();
    }

    /// <summary>Raised with status messages.</summary>
    public event EventHandler<string>? Message;

    /// <summary>Registered assistants.</summary>
    public ObservableCollection<AssistantItem> Assistants { get; } = [];

    /// <summary>Proposals of the last run.</summary>
    public ObservableCollection<ProposalItem> Proposals { get; } = [];

    /// <summary>Diagnostics of the last run.</summary>
    public ObservableCollection<string> Diagnostics { get; } = [];

    /// <summary>The selected assistant.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunForCurrentCommand), nameof(RunForAllCommand))]
    public partial AssistantItem? SelectedAssistant { get; set; }

    /// <summary>The selected proposal (previewed).</summary>
    [ObservableProperty]
    public partial ProposalItem? SelectedProposal { get; set; }

    /// <summary>True while an assistant runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunForCurrentCommand), nameof(RunForAllCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Status line of the panel.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>True when at least one assistant is registered.</summary>
    public bool HasAssistants => Assistants.Count > 0;

    /// <summary>True when the empty state is shown.</summary>
    public bool ShowEmptyState => Assistants.Count == 0;

    /// <summary>True when proposals exist.</summary>
    public bool HasProposals => Proposals.Count > 0;

    /// <summary>True when a merge is open (assistants only work on merges).</summary>
    public bool IsMergeOpen => _document() is MergeDocument;

    /// <summary>Re-reads the registry.</summary>
    public void RefreshAssistants()
    {
        string? selectedId = SelectedAssistant?.Assistant.Id;
        Assistants.Clear();
        foreach (IMergeAssistant a in _plugins.Manager.MergeAssistants.Assistants)
        {
            Assistants.Add(new AssistantItem(a));
        }

        SelectedAssistant = Assistants.FirstOrDefault(a => a.Assistant.Id == selectedId) ?? Assistants.FirstOrDefault();
        OnPropertyChanged(nameof(HasAssistants));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsMergeOpen));
    }

    /// <summary>Notifies that the active document changed.</summary>
    public void OnDocumentChanged()
    {
        ClearProposals();
        OnPropertyChanged(nameof(IsMergeOpen));
        RunForCurrentCommand.NotifyCanExecuteChanged();
        RunForAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsBusy && SelectedAssistant is not null && _document() is MergeDocument;

    /// <summary>Runs the selected assistant for the current conflict.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public Task RunForCurrentAsync()
    {
        if (_document() is not MergeDocument doc)
        {
            return Task.CompletedTask;
        }

        int region = doc.CurrentRegion;
        if (region < 0 || !doc.Session.Regions[region].IsChange)
        {
            if (!doc.GoToNextUnresolvedConflict())
            {
                StatusText = "Select a conflict first.";
                return Task.CompletedTask;
            }

            region = doc.CurrentRegion;
        }

        return RunAsync(doc, [region]);
    }

    /// <summary>Runs the selected assistant for all unresolved conflicts.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    public Task RunForAllAsync() => _document() is MergeDocument doc ? RunAsync(doc, []) : Task.CompletedTask;

    private async Task RunAsync(MergeDocument doc, IReadOnlyList<int> regions)
    {
        IMergeAssistant assistant = SelectedAssistant!.Assistant;
        IsBusy = true;
        StatusText = $"Running {assistant.DisplayName}…";
        ClearProposals();
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        try
        {
            MergeAssistantRequest request = new() { Snapshot = doc.CreateSnapshot(), TargetRegionIds = regions };
            MergeAssistantRunResult run = await _plugins.Manager.RunAssistantAsync(assistant, request, null, _runCts.Token).ConfigureAwait(true);
            ShowResult(doc, run.Result, assistant.DisplayName);
            StatusText = run.Succeeded
                ? $"{run.Result.Proposals.Count} proposal(s) from {assistant.DisplayName} in {run.Elapsed.TotalMilliseconds:0} ms"
                : $"{assistant.DisplayName} failed — see diagnostics";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows proposals offered by a plugin command; completes with the number accepted once all are decided.</summary>
    public Task<int> OfferAsync(MergeAssistantResult result, string source, CancellationToken cancellationToken)
    {
        if (_document() is not MergeDocument doc)
        {
            return Task.FromResult(0);
        }

        ClearProposals();
        ShowResult(doc, result, source);
        if (Proposals.Count == 0)
        {
            return Task.FromResult(0);
        }

        _offer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _offerAccepted = 0;
        cancellationToken.Register(() => _offer?.TrySetCanceled());
        return _offer.Task;
    }

    private void ShowResult(MergeDocument doc, MergeAssistantResult result, string source)
    {
        Core.Merge.MergeOutput output = doc.OutputSource.Output;
        foreach (ResolutionProposal p in result.Proposals)
        {
            if (p.RegionId < 0 || p.RegionId >= doc.Session.Regions.Count)
            {
                continue;
            }

            LineRange range = output.GetOutputRange(p.RegionId);
            IReadOnlyList<string> current = [.. output.Lines.Skip(range.Start).Take(range.Count)];
            (List<string> lines, List<HighlightKind> kinds) = BuildPreview(current, p.ReplacementLines, doc.Options);
            int conflictNo = doc.Session.Regions.Take(p.RegionId + 1).Count(r => r.IsConflict);
            string label = doc.Session.Regions[p.RegionId].IsConflict
                ? $"Conflict {conflictNo} · output line {range.Start + 1}"
                : $"Region {p.RegionId} · output line {range.Start + 1}";
            Proposals.Add(new ProposalItem(p, source, label, lines, kinds));
        }

        foreach (AssistantDiagnostic d in result.Diagnostics)
        {
            Diagnostics.Add($"{d.Severity}: {d.Message}");
        }

        SelectedProposal = Proposals.FirstOrDefault();
        OnPropertyChanged(nameof(HasProposals));
    }

    /// <summary>Builds a unified preview of current region output vs proposal.</summary>
    public static (List<string> Lines, List<HighlightKind> Kinds) BuildPreview(IReadOnlyList<string> current, IReadOnlyList<string> proposed, DiffOptions options)
    {
        List<string> lines = [];
        List<HighlightKind> kinds = [];
        if (current.Count == 0)
        {
            lines.Add("  (unresolved — no output yet)");
            kinds.Add(HighlightKind.Conflict);
            foreach (string l in proposed)
            {
                lines.Add("+ " + l);
                kinds.Add(HighlightKind.Inserted);
            }

            return (lines, kinds);
        }

        DiffResult diff = LineDiffer.Diff(current, proposed, options with { Whitespace = WhitespaceMode.None, IgnoreCase = false });
        int a = 0, b = 0;
        foreach (DiffHunk h in diff.Hunks)
        {
            for (; a < h.StartA; a++, b++)
            {
                lines.Add("  " + current[a]);
                kinds.Add(HighlightKind.None);
            }

            for (int i = 0; i < h.CountA; i++, a++)
            {
                lines.Add("- " + current[a]);
                kinds.Add(HighlightKind.Deleted);
            }

            for (int i = 0; i < h.CountB; i++, b++)
            {
                lines.Add("+ " + proposed[b]);
                kinds.Add(HighlightKind.Inserted);
            }
        }

        for (; a < current.Count; a++)
        {
            lines.Add("  " + current[a]);
            kinds.Add(HighlightKind.None);
        }

        if (diff.Hunks.Count == 0)
        {
            lines.Insert(0, "  (identical to the current output)");
            kinds.Insert(0, HighlightKind.Info);
        }

        return (lines, kinds);
    }

    /// <summary>Accepts a proposal: applies it to the merge (undoable).</summary>
    [RelayCommand]
    public void Accept(ProposalItem? item)
    {
        if (item is null || !item.IsPending || _document() is not MergeDocument doc)
        {
            return;
        }

        int applied = doc.Session.ApplyProposals([item.Proposal]);
        item.State = applied > 0 ? ProposalState.Accepted : ProposalState.Rejected;
        if (applied > 0)
        {
            _offerAccepted++;
            doc.SelectRegion(item.Proposal.RegionId);
            Message?.Invoke(this, $"Applied proposal for {item.RegionLabel} (Ctrl+Z to undo).");
        }

        CompleteOfferIfDone();
    }

    /// <summary>Rejects a proposal.</summary>
    [RelayCommand]
    public void Reject(ProposalItem? item)
    {
        if (item is null || !item.IsPending)
        {
            return;
        }

        item.State = ProposalState.Rejected;
        CompleteOfferIfDone();
    }

    /// <summary>Accepts all pending proposals in one undo step.</summary>
    [RelayCommand]
    public void AcceptAll()
    {
        if (_document() is not MergeDocument doc)
        {
            return;
        }

        List<ProposalItem> pending = [.. Proposals.Where(p => p.IsPending)];
        if (pending.Count == 0)
        {
            return;
        }

        int applied = doc.Session.ApplyProposals(pending.Select(p => p.Proposal));
        foreach (ProposalItem p in pending)
        {
            p.State = ProposalState.Accepted;
        }

        _offerAccepted += applied;
        Message?.Invoke(this, $"Applied {applied} proposal(s) (Ctrl+Z to undo).");
        CompleteOfferIfDone();
    }

    /// <summary>Removes all proposals.</summary>
    [RelayCommand]
    public void ClearProposals()
    {
        Proposals.Clear();
        Diagnostics.Clear();
        SelectedProposal = null;
        OnPropertyChanged(nameof(HasProposals));
        _offer?.TrySetResult(_offerAccepted);
        _offer = null;
    }

    private void CompleteOfferIfDone()
    {
        if (_offer is not null && Proposals.All(p => !p.IsPending))
        {
            _offer.TrySetResult(_offerAccepted);
            _offer = null;
        }
    }
}
