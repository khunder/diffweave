using DiffWeave.App.Editor;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Merge;
using DiffWeave.Core.Text;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Documents;

/// <summary>
/// A merge of base (A), left (B) and right (C) into an editable output. Two-way merges use an empty base.
/// Pane indexes match <see cref="MergeAlignment"/>: 0 = base, 1 = left, 2 = right, 3 = output.
/// </summary>
public sealed class MergeDocument : DiffDocument
{
    /// <summary>Output pane index.</summary>
    public const int OutputPane = MergeAlignment.OutputPane;

    private readonly ReadOnlyTextSource[] _inputSources;
    private readonly AlignmentDecorationSource[] _decorations = new AlignmentDecorationSource[3];
    private readonly OutputDecorationSource _outputDecorations;
    private readonly PaneModel _outputPane;
    private long _version;
    private long _savedVersion;

    private MergeDocument(IReadOnlyList<InputFile> inputs, MergeSession session, string? outputPath, string? outputLabel, bool hasBase)
    {
        Inputs = inputs;
        Session = session;
        OutputPath = outputPath;
        HasBase = hasBase;
        _inputSources = [.. inputs.Select(i => new ReadOnlyTextSource(i.Lines))];
        OutputSource = new MergeOutputSource(session);
        string[] badges = ["A", "B", "C"];
        string[] keys = ["DW.Badge.A", "DW.Badge.B", "DW.Badge.C"];
        PaneRole[] roles = [PaneRole.Base, PaneRole.Left, PaneRole.Right];
        List<PaneModel> panes = [];
        for (int i = 0; i < 3; i++)
        {
            PaneModel p = new(i, roles[i], badges[i], keys[i], _inputSources[i], inputs[i]);
            DescribePane(p, inputs[i]);
            _decorations[i] = new AlignmentDecorationSource(() => Mapping, i, Partner, pane => pane == OutputPane ? OutputSource : _inputSources[pane])
            {
                KindSelector = InputKind,
            };
            p.DiffDecorations = _decorations[i];
            panes.Add(p);
        }

        _outputPane = new PaneModel(OutputPane, PaneRole.Output, "Output", "DW.Badge.Out", OutputSource, null)
        {
            Title = outputLabel ?? (outputPath is null ? "Merge result (unsaved)" : Path.GetFileName(outputPath)),
            Path = outputPath,
        };
        _outputDecorations = new OutputDecorationSource(this);
        _outputPane.DiffDecorations = _outputDecorations;
        panes.Add(_outputPane);
        Panes = panes;
        session.Changed += OnSessionChanged;
        Rebuild();
        _savedVersion = -1;
    }

    /// <summary>The merge session (resolution state and undo history).</summary>
    public MergeSession Session { get; }

    /// <summary>The output text source.</summary>
    public MergeOutputSource OutputSource { get; }

    /// <summary>Where the result is saved; null asks on save.</summary>
    public string? OutputPath { get; set; }

    /// <summary>False for two-way merges (no base file).</summary>
    public bool HasBase { get; }

    /// <summary>Optional lifecycle hooks invoked around resolve and save.</summary>
    public IMergeLifecycleHookRegistry? LifecycleHooks { get; set; }

    /// <inheritdoc />
    public override EditorKind Kind => EditorKind.Merge;

    /// <summary>True when the output changed since it was last saved (a new merge is dirty until saved).</summary>
    public bool IsDirty => _version != _savedVersion;

    /// <summary>True when the result was saved at least once.</summary>
    public bool WasSaved { get; private set; }

    /// <summary>True when the last save happened without unresolved regions.</summary>
    public bool SavedResolved { get; private set; }

    /// <summary>Number of conflicts.</summary>
    public int ConflictCount => Session.ConflictCount;

    /// <summary>Number of unresolved regions (conflicts and, without auto-merge, other changes).</summary>
    public int UnresolvedCount => Session.UnresolvedCount;

    /// <summary>The output pane.</summary>
    public PaneModel OutputPaneModel => _outputPane;

    /// <inheritdoc />
    public override string Title => OutputPath is null ? "Merge" : $"Merge → {Path.GetFileName(OutputPath)}";

    /// <summary>Creates a merge document; auto-merges unless <paramref name="autoMerge"/> is false.</summary>
    public static async Task<MergeDocument> CreateAsync(InputFile? baseFile, InputFile left, InputFile right, string? outputPath, DiffOptions options, bool autoMerge = true, string? outputLabel = null, CancellationToken cancellationToken = default)
    {
        InputFile b = baseFile ?? InputFile.Empty("(no base)");
        MergeOptions mo = new() { Diff = options };
        MergeSession session = await Task.Run(() => MergeSession.Create(b.Lines, left.Lines, right.Lines, mo, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (autoMerge)
        {
            session.AutoResolve();
        }

        MergeDocument doc = new([b, left, right], session, outputPath, outputLabel, baseFile is not null) { Options = options };
        return doc;
    }

    /// <summary>Synchronous creation for non-interactive use.</summary>
    public static MergeDocument Create(InputFile? baseFile, InputFile left, InputFile right, string? outputPath, DiffOptions options, bool autoMerge = true)
    {
        InputFile b = baseFile ?? InputFile.Empty("(no base)");
        MergeSession session = MergeSession.Create(b.Lines, left.Lines, right.Lines, new MergeOptions { Diff = options });
        if (autoMerge)
        {
            session.AutoResolve();
        }

        return new MergeDocument([b, left, right], session, outputPath, null, baseFile is not null) { Options = options };
    }

    /// <summary>The region of a block.</summary>
    public int RegionOfBlock(DiffBlock block) => OutputSource.Output.Alignment.GetRegion(block.StartRow);

    /// <summary>The region of the current block, or -1.</summary>
    public int CurrentRegion => Current is { } b ? RegionOfBlock(b) : -1;

    /// <summary>The block showing a region, or null.</summary>
    public DiffBlock? BlockOfRegion(int region)
    {
        if (Mapping is null || region < 0)
        {
            return null;
        }

        foreach (DiffBlock b in Mapping.Blocks)
        {
            if (RegionOfBlock(b) == region)
            {
                return b;
            }
        }

        return null;
    }

    /// <summary>True when a block's region is unresolved.</summary>
    public bool IsUnresolved(DiffBlock b) => !Session.GetResolution(RegionOfBlock(b)).IsResolved;

    /// <summary>True when a block's region is a conflict.</summary>
    public bool IsConflictBlock(DiffBlock b) => Session.Regions[RegionOfBlock(b)].IsConflict;

    /// <summary>Selects the next unresolved conflict (wrapping); false when none.</summary>
    public bool GoToNextUnresolvedConflict()
    {
        int next = Session.FindNextUnresolvedConflict(CurrentRegion);
        if (next < 0)
        {
            next = Session.FindNextUnresolvedConflict(-1);
        }

        return SelectRegion(next);
    }

    /// <summary>Selects the previous unresolved conflict (wrapping); false when none.</summary>
    public bool GoToPreviousUnresolvedConflict()
    {
        int cur = CurrentRegion < 0 ? Session.Regions.Count : CurrentRegion;
        int prev = Session.FindPreviousUnresolvedConflict(cur);
        if (prev < 0)
        {
            prev = Session.FindPreviousUnresolvedConflict(Session.Regions.Count);
        }

        return SelectRegion(prev);
    }

    /// <summary>Selects the block of a region.</summary>
    public bool SelectRegion(int region)
    {
        if (BlockOfRegion(region) is { } b)
        {
            CurrentBlock = b.Index;
            return true;
        }

        return false;
    }

    /// <summary>Resolves the current region (or the first unresolved conflict when there is no current block).</summary>
    public async Task<bool> ChooseAsync(ResolutionChoice choice)
    {
        int region = CurrentRegion;
        if (region < 0)
        {
            if (!GoToNextUnresolvedConflict())
            {
                return false;
            }

            region = CurrentRegion;
        }

        MergeLifecycleContext? ctx = LifecycleHooks is null ? null : new MergeLifecycleContext(CreateSnapshot(), region, choice.ToString());
        if (LifecycleHooks is not null)
        {
            HookResult r = await LifecycleHooks.RunBeforeResolveAsync(ctx!).ConfigureAwait(true);
            if (!r.Proceed)
            {
                ShowMessage(r.Message ?? "A plugin cancelled the resolution.", MessageSeverity.Warning);
                return false;
            }
        }

        if (choice == ResolutionChoice.Unresolved)
        {
            Session.Unresolve(region);
        }
        else
        {
            Session.Resolve(region, choice);
        }

        SelectRegion(region);
        if (LifecycleHooks is not null)
        {
            await LifecycleHooks.RunAfterResolveAsync(new MergeLifecycleContext(CreateSnapshot(), region, choice.ToString())).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>Runs the automatic merge over all unresolved regions.</summary>
    public int AutoMergeAll() => Session.AutoResolve();

    /// <summary>Resolves every unresolved region with one choice in one undo step.</summary>
    public int ResolveAll(ResolutionChoice choice)
    {
        int n = 0;
        using (Session.BeginBatch())
        {
            for (int i = 0; i < Session.Regions.Count; i++)
            {
                if (!Session.GetResolution(i).IsResolved)
                {
                    Session.Resolve(i, choice);
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Lines of the result to write: conflict markers when regions remain unresolved.</summary>
    public IReadOnlyList<string> GetLinesToSave()
        => Session.UnresolvedCount == 0
            ? OutputSource.Output.Lines
            : Session.ExportWithConflictMarkers(ConflictMarkerStyle.Merge, new ConflictMarkerLabels(Inputs[1].Label, Inputs[0].Label, Inputs[2].Label));

    /// <summary>
    /// Saves the result, keeping the encoding, BOM and line endings of the left (local) input (or of the base when
    /// left is empty). Runs before-save hooks; returns false when a hook vetoed.
    /// </summary>
    public async Task<bool> SaveAsync(string? path = null)
    {
        path ??= OutputPath ?? throw new InvalidOperationException("No output path.");
        if (LifecycleHooks is not null)
        {
            HookResult r = await LifecycleHooks.RunBeforeSaveAsync(new MergeLifecycleContext(CreateSnapshot())).ConfigureAwait(true);
            if (!r.Proceed)
            {
                ShowMessage(r.Message ?? "A plugin prevented saving.", MessageSeverity.Warning);
                return false;
            }
        }

        SaveCore(path);
        return true;
    }

    /// <summary>Saves synchronously without hooks (non-interactive path).</summary>
    public void SaveCore(string path)
    {
        TextDocument template = Inputs[1].Document.LineCount > 0 || Inputs[0].Document.LineCount == 0 ? Inputs[1].Document : Inputs[0].Document;
        TextDocument result = template.WithLines(GetLinesToSave());
        result.Save(path);
        OutputPath = path;
        _outputPane.Path = path;
        _outputPane.Title = Path.GetFileName(path);
        _savedVersion = _version;
        WasSaved = true;
        SavedResolved = Session.UnresolvedCount == 0;
        OnStateChanged();
    }

    /// <summary>Creates a plugin snapshot.</summary>
    public MergeSnapshot CreateSnapshot()
        => Session.CreateSnapshot(Inputs[0].Path ?? Inputs[0].Label, Inputs[1].Path ?? Inputs[1].Label, Inputs[2].Path ?? Inputs[2].Label, OutputPath, LanguageId ?? "plaintext");

    /// <inheritdoc />
    public override MergeSnapshot? GetMergeSnapshot() => CreateSnapshot();

    /// <inheritdoc />
    public override Task RecomputeAsync(CancellationToken cancellationToken = default)
    {
        // A merge keeps its session (and undo history); options only affect the inline highlighting here.
        Rebuild();
        return Task.CompletedTask;
    }

    private void OnSessionChanged(object? sender, MergeChangedEventArgs e)
    {
        int region = CurrentRegion;
        _version++;
        Rebuild();
        if (region >= 0)
        {
            SelectRegion(region);
        }

        OnStateChanged();
    }

    private void Rebuild()
    {
        MergeOutput output = OutputSource.Output;
        Mapping = new PaneMapping(output.Alignment, Classify);
        List<PhantomRow> phantoms = [];
        for (int i = 0; i < Session.Regions.Count; i++)
        {
            MergeRegion region = Session.Regions[i];
            if (!Session.GetResolution(i).IsResolved)
            {
                string label = region.IsConflict ? "unresolved conflict — choose A, B or C (Ctrl+1/2/3)" : "unresolved change — choose A, B or C";
                phantoms.Add(new PhantomRow(output.GetOutputRange(i).Start, label, HighlightKind.Conflict));
            }
        }

        _outputPane.PhantomRows = phantoms;
        _outputPane.Info = $"{OutputSource.LineCount:N0} lines";
        foreach (AlignmentDecorationSource d in _decorations)
        {
            d.Invalidate(Options);
        }

        _outputDecorations.Invalidate();
        OnMappingChanged();
    }

    private (HighlightKind, IReadOnlyList<HighlightKind>?) Classify(DifferenceGroup g, int[]? panes)
    {
        int region = OutputSource.Output.Alignment.GetRegion(g.StartRow);
        MergeRegion r = Session.Regions[region];
        HighlightKind outKind = OutputKind(region);
        return r.Kind switch
        {
            RegionKind.Conflict => (HighlightKind.Conflict, [HighlightKind.Conflict, HighlightKind.Conflict, HighlightKind.Conflict, outKind]),
            RegionKind.LeftChanged => (HighlightKind.SourceB, [HighlightKind.SourceA, HighlightKind.SourceB, HighlightKind.None, outKind]),
            RegionKind.RightChanged => (HighlightKind.SourceC, [HighlightKind.SourceA, HighlightKind.None, HighlightKind.SourceC, outKind]),
            RegionKind.BothChangedSame => (HighlightKind.Changed, [HighlightKind.SourceA, HighlightKind.SourceB, HighlightKind.SourceC, outKind]),
            _ => (HighlightKind.None, [HighlightKind.None, HighlightKind.None, HighlightKind.None, outKind]),
        };
    }

    /// <summary>The highlight of a region's output lines according to its resolution.</summary>
    public HighlightKind OutputKind(int region)
    {
        MergeRegion r = Session.Regions[region];
        RegionResolution res = Session.GetResolution(region);
        if (!res.IsResolved)
        {
            return HighlightKind.Conflict;
        }

        if (r.Kind == RegionKind.Stable)
        {
            return HighlightKind.None;
        }

        return res.Choice switch
        {
            ResolutionChoice.Base => HighlightKind.SourceA,
            ResolutionChoice.Left => HighlightKind.SourceB,
            ResolutionChoice.Right => HighlightKind.SourceC,
            ResolutionChoice.Custom => HighlightKind.Resolved,
            _ => HighlightKind.Changed,
        };
    }

    private HighlightKind InputKind(DiffBlock b, int pane) => b.KindFor(pane);

    private int? Partner(int row, int pane)
    {
        if (pane == 0)
        {
            int left = Mapping!.Alignment.GetLine(row, 1);
            int baseLine = Mapping.Alignment.GetLine(row, 0);
            return left >= 0 && baseLine >= 0 && !string.Equals(_inputSources[1].GetLine(left), _inputSources[0].GetLine(baseLine), StringComparison.Ordinal) ? 1 : 2;
        }

        return HasBase ? 0 : (pane == 1 ? 2 : 1);
    }

    /// <summary>Decorations of the output pane: line colours by resolution plus a gutter mark on resolved conflicts.</summary>
    private sealed class OutputDecorationSource(MergeDocument doc) : IEditorDecorationSource
    {
        public event EventHandler? Invalidated;

        public void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

        public void GetDecorations(int firstLine, int lastLine, DecorationSink sink)
        {
            MergeOutput o = doc.OutputSource.Output;
            int last = Math.Min(lastLine, o.Lines.Count - 1);
            int prevRegion = -1;
            for (int line = Math.Max(0, firstLine); line <= last; line++)
            {
                int region = o.GetRegionOfLine(line);
                HighlightKind k = doc.OutputKind(region);
                if (k != HighlightKind.None)
                {
                    sink.Lines.Add(new LineDecoration(line, k));
                }

                if (region != prevRegion && doc.Session.Regions[region].IsConflict && doc.Session.GetResolution(region).IsResolved && o.GetOutputRange(region).Start == line)
                {
                    sink.Marks.Add(new GutterMark(line, HighlightKind.Resolved, "Resolved conflict"));
                }

                prevRegion = region;
            }
        }
    }
}
