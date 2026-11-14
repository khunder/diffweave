using DiffWeave.Core.Diff;
using DiffWeave.Extensibility;

namespace DiffWeave.Core.Merge;

/// <summary>
/// A three-way merge: the immutable list of regions computed from base, left and right, plus the mutable resolution
/// state of every region with undo/redo. Not thread-safe: use it from one thread (the UI thread); the
/// <see cref="MergeOutput"/> and snapshots it hands out are immutable and may be used anywhere.
/// </summary>
/// <remarks>
/// <para>Stable regions start resolved to <see cref="ResolutionChoice.Base"/>; all other regions start unresolved.
/// Call <see cref="AutoResolve"/> to apply KDiff3's automatic merge.</para>
/// <para>Hand edits of the output (<see cref="ApplyManualEdit"/>) are mapped back to regions: see that method.</para>
/// <para>When the diff options ignore whitespace or case, lines that compare equal belong to stable regions and the
/// output takes the base text for them.</para>
/// </remarks>
public sealed class MergeSession
{
    private readonly MergeRegion[] _regions;
    private readonly RegionResolution[] _resolutions;
    private readonly List<Operation> _undo = [];
    private readonly List<Operation> _redo = [];
    private readonly IReadOnlyList<Diff3Chunk> _chunks;
    private MergeOutput? _output;
    private int _batchDepth;
    private List<(int Region, RegionResolution Before, RegionResolution After)>? _batchChanges;

    private MergeSession(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, MergeOptions options, IReadOnlyList<Diff3Chunk> chunks)
    {
        BaseLines = baseLines;
        LeftLines = left;
        RightLines = right;
        Options = options;
        _chunks = chunks;
        _regions = new MergeRegion[chunks.Count];
        _resolutions = new RegionResolution[chunks.Count];
        for (int i = 0; i < chunks.Count; i++)
        {
            Diff3Chunk c = chunks[i];
            RegionKind kind = c.Change switch
            {
                ThreeWayChange.None => RegionKind.Stable,
                ThreeWayChange.LeftChanged => RegionKind.LeftChanged,
                ThreeWayChange.RightChanged => RegionKind.RightChanged,
                ThreeWayChange.BothChangedSame => RegionKind.BothChangedSame,
                _ => RegionKind.Conflict,
            };
            bool wsOnly = kind == RegionKind.Conflict && Diff3.RangesEqualIgnoringWhitespace(left, c.Left, right, c.Right);
            _regions[i] = new MergeRegion(i, kind, c.Base, c.Left, c.Right, wsOnly);
            _resolutions[i] = kind == RegionKind.Stable ? new RegionResolution(ResolutionChoice.Base) : RegionResolution.Unresolved;
        }

        ConflictCount = _regions.Count(r => r.IsConflict);
    }

    /// <summary>Creates a session. Runs the two diffs synchronously (use a background thread for big files).</summary>
    public static MergeSession Create(IReadOnlyList<string> baseLines, IReadOnlyList<string> left, IReadOnlyList<string> right, MergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseLines);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= MergeOptions.Default;
        string[] b = [.. baseLines], l = [.. left], r = [.. right];
        IReadOnlyList<Diff3Chunk> chunks = Diff3.Compute(b, l, r, options.Diff, cancellationToken);
        return new MergeSession(b, l, r, options, chunks);
    }

    /// <summary>The options the session was created with.</summary>
    public MergeOptions Options { get; }

    /// <summary>Base lines.</summary>
    public IReadOnlyList<string> BaseLines { get; }

    /// <summary>Left lines.</summary>
    public IReadOnlyList<string> LeftLines { get; }

    /// <summary>Right lines.</summary>
    public IReadOnlyList<string> RightLines { get; }

    /// <summary>The regions, covering all input lines in order.</summary>
    public IReadOnlyList<MergeRegion> Regions => _regions;

    /// <summary>Number of conflict regions (resolved or not).</summary>
    public int ConflictCount { get; }

    /// <summary>Number of unresolved regions (of any kind).</summary>
    public int UnresolvedCount => _resolutions.Count(r => !r.IsResolved);

    /// <summary>Number of unresolved conflict regions.</summary>
    public int UnresolvedConflictCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _regions.Length; i++)
            {
                if (_regions[i].IsConflict && !_resolutions[i].IsResolved)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Whether <see cref="Undo"/> is possible.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether <see cref="Redo"/> is possible.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every change of the resolution state (including undo/redo).</summary>
    public event EventHandler<MergeChangedEventArgs>? Changed;

    /// <summary>The resolution of a region.</summary>
    public RegionResolution GetResolution(int region) => _resolutions[CheckRegion(region)];

    /// <summary>The lines a region would produce with the given choice (custom: the current custom lines, or empty).</summary>
    public IReadOnlyList<string> GetLinesForChoice(int region, ResolutionChoice choice)
    {
        MergeRegion r = _regions[CheckRegion(region)];
        return choice switch
        {
            ResolutionChoice.Base => Slice(BaseLines, r.Base),
            ResolutionChoice.Left => Slice(LeftLines, r.Left),
            ResolutionChoice.Right => Slice(RightLines, r.Right),
            ResolutionChoice.LeftThenRight => [.. Slice(LeftLines, r.Left), .. Slice(RightLines, r.Right)],
            ResolutionChoice.RightThenLeft => [.. Slice(RightLines, r.Right), .. Slice(LeftLines, r.Left)],
            ResolutionChoice.Custom => _resolutions[region].CustomLines ?? [],
            _ => [],
        };
    }

    /// <summary>The lines a region currently contributes to the output.</summary>
    public IReadOnlyList<string> GetCurrentLines(int region) => GetLinesFor(region, _resolutions[CheckRegion(region)]);

    /// <summary>
    /// Automatic merge with KDiff3 semantics: resolves unresolved single-sided changes to the changed side, identical
    /// changes to (the preferred side of) both, and - with <see cref="MergeOptions.AutoSolveWhitespaceConflicts"/> -
    /// whitespace-only conflicts to the preferred side. Already resolved regions are left alone. One undo step.
    /// </summary>
    /// <returns>The number of regions resolved.</returns>
    public int AutoResolve()
    {
        var changes = new List<(int, RegionResolution, RegionResolution)>();
        ResolutionChoice preferred = Options.WhitespaceConflictPreference == WhitespaceConflictPreference.Left ? ResolutionChoice.Left : ResolutionChoice.Right;
        for (int i = 0; i < _regions.Length; i++)
        {
            if (_resolutions[i].IsResolved)
            {
                continue;
            }

            MergeRegion r = _regions[i];
            ResolutionChoice? choice = r.Kind switch
            {
                RegionKind.LeftChanged => ResolutionChoice.Left,
                RegionKind.RightChanged => ResolutionChoice.Right,
                RegionKind.BothChangedSame => preferred,
                RegionKind.Conflict when r.IsWhitespaceOnlyConflict && Options.AutoSolveWhitespaceConflicts => preferred,
                RegionKind.Stable => ResolutionChoice.Base,
                _ => null,
            };
            if (choice is { } c)
            {
                changes.Add((i, _resolutions[i], new RegionResolution(c)));
            }
        }

        Apply(changes, MergeChangeKind.AutoResolve);
        return changes.Count;
    }

    /// <summary>Sets the resolution of a region (one undo step). <see cref="ResolutionChoice.Custom"/> requires <see cref="ResolveCustom"/>.</summary>
    public void Resolve(int region, ResolutionChoice choice)
    {
        CheckRegion(region);
        if (choice == ResolutionChoice.Custom)
        {
            throw new ArgumentException("Use ResolveCustom to set custom lines.", nameof(choice));
        }

        if (choice == ResolutionChoice.Unresolved)
        {
            Unresolve(region);
            return;
        }

        Apply([(region, _resolutions[region], new RegionResolution(choice))], MergeChangeKind.Resolve);
    }

    /// <summary>Resolves a region to custom lines (one undo step).</summary>
    public void ResolveCustom(int region, IReadOnlyList<string> lines)
    {
        CheckRegion(region);
        ArgumentNullException.ThrowIfNull(lines);
        Apply([(region, _resolutions[region], new RegionResolution(ResolutionChoice.Custom, [.. lines]))], MergeChangeKind.Resolve);
    }

    /// <summary>Resets a region to unresolved (one undo step). Stable regions revert to <see cref="ResolutionChoice.Base"/>.</summary>
    public void Unresolve(int region)
    {
        CheckRegion(region);
        RegionResolution target = _regions[region].Kind == RegionKind.Stable ? new RegionResolution(ResolutionChoice.Base) : RegionResolution.Unresolved;
        Apply([(region, _resolutions[region], target)], MergeChangeKind.Unresolve);
    }

    /// <summary>
    /// Applies a hand edit of the output: lines <c>[outputStart, outputStart+removedCount)</c> of the current output
    /// are replaced by <paramref name="insertedLines"/>. One undo step.
    /// </summary>
    /// <remarks>
    /// Mapping rules: the affected regions are those whose output range intersects the edited range. A pure
    /// insertion (<paramref name="removedCount"/> = 0) at position p goes to an unresolved region positioned at p if
    /// there is one, otherwise to the region owning line p-1 (appending), otherwise (p = 0) to the region owning
    /// line 0. The first affected region becomes <see cref="ResolutionChoice.Custom"/> with its unedited prefix, the
    /// inserted lines and the unedited suffix of the last affected region; the other affected regions become
    /// <see cref="ResolutionChoice.Custom"/> with no lines.
    /// </remarks>
    /// <returns>The indexes of the affected regions.</returns>
    public IReadOnlyList<int> ApplyManualEdit(int outputStart, int removedCount, IReadOnlyList<string> insertedLines)
    {
        ArgumentNullException.ThrowIfNull(insertedLines);
        MergeOutput output = BuildOutput();
        int total = output.Lines.Count;
        if (outputStart < 0 || removedCount < 0 || outputStart + removedCount > total)
        {
            throw new ArgumentOutOfRangeException(nameof(outputStart), "The edited range lies outside the output.");
        }

        if (_regions.Length == 0)
        {
            throw new InvalidOperationException("An empty merge has no region to hold the edit.");
        }

        var affected = new List<int>();
        if (removedCount == 0)
        {
            int target = -1;
            for (int i = 0; i < _regions.Length; i++)
            {
                if (!_resolutions[i].IsResolved && output.GetOutputRange(i).Start == outputStart)
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
            {
                target = outputStart > 0 ? output.GetRegionOfLine(outputStart - 1)
                    : total > 0 ? output.GetRegionOfLine(0)
                    : 0;
            }

            affected.Add(target);
        }
        else
        {
            int end = outputStart + removedCount;
            for (int i = 0; i < _regions.Length; i++)
            {
                LineRange r = output.GetOutputRange(i);
                if (r.Count > 0 && r.Start < end && r.End > outputStart)
                {
                    affected.Add(i);
                }
            }
        }

        int first = affected[0], last = affected[^1];
        LineRange firstRange = output.GetOutputRange(first);
        LineRange lastRange = output.GetOutputRange(last);
        var newLines = new List<string>();
        for (int k = firstRange.Start; k < outputStart; k++)
        {
            newLines.Add(output.Lines[k]);
        }

        newLines.AddRange(insertedLines);
        int suffixStart = Math.Max(outputStart + removedCount, lastRange.Start);
        if (removedCount == 0)
        {
            suffixStart = outputStart;
        }

        for (int k = suffixStart; k < lastRange.End; k++)
        {
            newLines.Add(output.Lines[k]);
        }

        var changes = new List<(int, RegionResolution, RegionResolution)>
        {
            (first, _resolutions[first], new RegionResolution(ResolutionChoice.Custom, [.. newLines])),
        };
        foreach (int i in affected.Skip(1))
        {
            changes.Add((i, _resolutions[i], new RegionResolution(ResolutionChoice.Custom, [])));
        }

        Apply(changes, MergeChangeKind.ManualEdit);
        return affected;
    }

    /// <summary>
    /// Applies accepted assistant proposals as custom resolutions in one undo step. Proposals for unknown regions are
    /// ignored. Returns the number applied.
    /// </summary>
    public int ApplyProposals(IEnumerable<ResolutionProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var changes = new List<(int, RegionResolution, RegionResolution)>();
        var seen = new HashSet<int>();
        foreach (ResolutionProposal p in proposals)
        {
            if (p is null || p.RegionId < 0 || p.RegionId >= _regions.Length || p.ReplacementLines is null || !seen.Add(p.RegionId))
            {
                continue;
            }

            changes.Add((p.RegionId, _resolutions[p.RegionId], new RegionResolution(ResolutionChoice.Custom, [.. p.ReplacementLines])));
        }

        Apply(changes, MergeChangeKind.Batch);
        return changes.Count;
    }

    /// <summary>
    /// Groups all operations until the returned object is disposed into a single undo step (nesting allowed).
    /// <see cref="Changed"/> is raised once at the end with <see cref="MergeChangeKind.Batch"/>.
    /// </summary>
    public IDisposable BeginBatch()
    {
        if (_batchDepth++ == 0)
        {
            _batchChanges = [];
        }

        return new BatchScope(this);
    }

    /// <summary>Undoes the last operation.</summary>
    public bool Undo()
    {
        if (_batchDepth > 0)
        {
            throw new InvalidOperationException("Cannot undo inside a batch.");
        }

        if (_undo.Count == 0)
        {
            return false;
        }

        Operation op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int k = op.Changes.Count - 1; k >= 0; k--)
        {
            _resolutions[op.Changes[k].Region] = op.Changes[k].Before;
        }

        _redo.Add(op);
        Raise(MergeChangeKind.Undo, op.Changes.Select(c => c.Region));
        return true;
    }

    /// <summary>Redoes the last undone operation.</summary>
    public bool Redo()
    {
        if (_batchDepth > 0)
        {
            throw new InvalidOperationException("Cannot redo inside a batch.");
        }

        if (_redo.Count == 0)
        {
            return false;
        }

        Operation op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach ((int region, RegionResolution _, RegionResolution after) in op.Changes)
        {
            _resolutions[region] = after;
        }

        _undo.Add(op);
        Raise(MergeChangeKind.Redo, op.Changes.Select(c => c.Region));
        return true;
    }

    /// <summary>The current output (cached until the next change).</summary>
    public MergeOutput BuildOutput() => _output ??= Render();

    /// <summary>The next unresolved conflict after <paramref name="region"/> (-1 to search from the start), or -1.</summary>
    public int FindNextUnresolvedConflict(int region)
    {
        for (int i = Math.Max(region + 1, 0); i < _regions.Length; i++)
        {
            if (_regions[i].IsConflict && !_resolutions[i].IsResolved)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The previous unresolved conflict before <paramref name="region"/>, or -1.</summary>
    public int FindPreviousUnresolvedConflict(int region)
    {
        for (int i = Math.Min(region - 1, _regions.Length - 1); i >= 0; i--)
        {
            if (_regions[i].IsConflict && !_resolutions[i].IsResolved)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The output with git-style conflict markers around unresolved conflicts (and unresolved changes, which are
    /// written as conflicts too). Resolved regions are written as resolved.
    /// </summary>
    public IReadOnlyList<string> ExportWithConflictMarkers(ConflictMarkerStyle style = ConflictMarkerStyle.Merge, ConflictMarkerLabels? labels = null)
    {
        labels ??= ConflictMarkerLabels.Default;
        var lines = new List<string>();
        for (int i = 0; i < _regions.Length; i++)
        {
            if (_resolutions[i].IsResolved)
            {
                lines.AddRange(GetLinesFor(i, _resolutions[i]));
                continue;
            }

            MergeRegion r = _regions[i];
            lines.Add(Marker('<', labels.Left));
            lines.AddRange(Slice(LeftLines, r.Left));
            if (style == ConflictMarkerStyle.Diff3)
            {
                lines.Add(Marker('|', labels.Base));
                lines.AddRange(Slice(BaseLines, r.Base));
            }

            lines.Add(new string('=', 7));
            lines.AddRange(Slice(RightLines, r.Right));
            lines.Add(Marker('>', labels.Right));
        }

        return lines;

        static string Marker(char c, string label) => string.IsNullOrEmpty(label) ? new string(c, 7) : new string(c, 7) + " " + label;
    }

    /// <summary>Creates the immutable snapshot handed to plugins.</summary>
    public MergeSnapshot CreateSnapshot(string? baseName = null, string? leftName = null, string? rightName = null, string? outputName = null, string languageId = "plaintext")
    {
        MergeOutput output = BuildOutput();
        var descriptors = new MergeRegionDescriptor[_regions.Length];
        for (int i = 0; i < _regions.Length; i++)
        {
            MergeRegion r = _regions[i];
            LineRange o = output.GetOutputRange(i);
            descriptors[i] = new MergeRegionDescriptor(
                i,
                (MergeRegionKind)(int)r.Kind,
                new LineSpan(r.Base.Start, r.Base.Count),
                new LineSpan(r.Left.Start, r.Left.Count),
                new LineSpan(r.Right.Start, r.Right.Count),
                new LineSpan(o.Start, o.Count),
                _resolutions[i].IsResolved,
                r.IsWhitespaceOnlyConflict);
        }

        return new MergeSnapshot
        {
            BaseLines = BaseLines,
            LeftLines = LeftLines,
            RightLines = RightLines,
            OutputLines = output.Lines,
            BaseName = baseName,
            LeftName = leftName,
            RightName = rightName,
            OutputName = outputName,
            LanguageId = languageId,
            Regions = descriptors,
        };
    }

    private int CheckRegion(int region)
    {
        if ((uint)region >= (uint)_regions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        return region;
    }

    private static string[] Slice(IReadOnlyList<string> lines, LineRange range)
    {
        var result = new string[range.Count];
        for (int k = 0; k < range.Count; k++)
        {
            result[k] = lines[range.Start + k];
        }

        return result;
    }

    private IReadOnlyList<string> GetLinesFor(int region, RegionResolution resolution)
        => resolution.Choice == ResolutionChoice.Custom ? resolution.CustomLines ?? [] : GetLinesForChoice(region, resolution.Choice);

    private void Apply(List<(int Region, RegionResolution Before, RegionResolution After)> changes, MergeChangeKind kind)
    {
        changes.RemoveAll(c => c.Before.Choice == c.After.Choice && SameCustom(c.Before, c.After));
        if (changes.Count == 0)
        {
            return;
        }

        foreach ((int region, RegionResolution _, RegionResolution after) in changes)
        {
            _resolutions[region] = after;
        }

        _output = null;
        if (_batchDepth > 0)
        {
            _batchChanges!.AddRange(changes);
            return;
        }

        _undo.Add(new Operation(changes));
        _redo.Clear();
        Raise(kind, changes.Select(c => c.Region));
    }

    private static bool SameCustom(RegionResolution a, RegionResolution b)
        => a.CustomLines is null ? b.CustomLines is null : b.CustomLines is not null && a.CustomLines.SequenceEqual(b.CustomLines);

    private void EndBatch()
    {
        if (_batchDepth == 0)
        {
            return;
        }

        if (--_batchDepth > 0)
        {
            return;
        }

        List<(int Region, RegionResolution Before, RegionResolution After)> changes = _batchChanges!;
        _batchChanges = null;
        if (changes.Count == 0)
        {
            return;
        }

        _undo.Add(new Operation(changes));
        _redo.Clear();
        Raise(MergeChangeKind.Batch, changes.Select(c => c.Region));
    }

    private void Raise(MergeChangeKind kind, IEnumerable<int> regions)
    {
        _output = null;
        Changed?.Invoke(this, new MergeChangedEventArgs(kind, [.. regions.Distinct().Order()]));
    }

    private MergeOutput Render()
    {
        var lines = new List<string>();
        var regionOfLine = new List<int>();
        var ranges = new LineRange[_regions.Length];
        var cols = new[] { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
        var kinds = new List<RowKind>();
        var regionOfRow = new List<int>();
        var firstRow = new int[_regions.Length];
        var unresolved = new bool[_regions.Length];
        DiffOptions diffOptions = Options.Diff;

        for (int i = 0; i < _regions.Length; i++)
        {
            MergeRegion region = _regions[i];
            RegionResolution res = _resolutions[i];
            IReadOnlyList<string> regionLines = GetLinesFor(i, res);
            int outStart = lines.Count;
            ranges[i] = new LineRange(outStart, regionLines.Count);
            foreach (string l in regionLines)
            {
                lines.Add(l);
                regionOfLine.Add(i);
            }

            unresolved[i] = !res.IsResolved;
            firstRow[i] = kinds.Count;

            Diff3Chunk chunk = _chunks[i];
            List<(int B, int L, int R)> layout = ThreeWayAlignment.LayoutChunk(BaseLines, LeftLines, RightLines, chunk);
            int[] outCol = PlaceOutput(layout, region, res.Choice, regionLines.Count, outStart, out int rowCount);
            if (rowCount == 0 && unresolved[i])
            {
                rowCount = 1;
            }

            for (int k = 0; k < rowCount; k++)
            {
                (int b, int l, int r) = k < layout.Count ? layout[k] : (-1, -1, -1);
                cols[0].Add(b);
                cols[1].Add(l);
                cols[2].Add(r);
                cols[3].Add(k < outCol.Length ? outCol[k] : -1);
                RowKind kind;
                if (region.Kind == RegionKind.Stable)
                {
                    kind = b >= 0 && l >= 0 && r >= 0 ? ThreeWayAlignment.ClassifyRow(BaseLines, LeftLines, RightLines, chunk, b, l, r, diffOptions) : RowKind.Changed;
                    if (res.Choice != ResolutionChoice.Base)
                    {
                        kind = RowKind.Changed; // hand-edited stable text
                    }
                }
                else if (k < layout.Count)
                {
                    kind = ThreeWayAlignment.ClassifyRow(BaseLines, LeftLines, RightLines, chunk, b, l, r, diffOptions);
                }
                else
                {
                    kind = region.IsConflict ? RowKind.Conflict : RowKind.Changed;
                }

                kinds.Add(kind);
                regionOfRow.Add(i);
            }
        }

        var alignment = new MergeAlignment([.. cols.Select(c => c.ToArray())], [.. kinds], [.. regionOfRow], firstRow, unresolved);
        return new MergeOutput(lines, [.. regionOfLine], ranges, alignment);
    }

    /// <summary>Assigns output lines to rows of a region layout; returns the output column and the row count needed.</summary>
    private static int[] PlaceOutput(List<(int B, int L, int R)> layout, MergeRegion region, ResolutionChoice choice, int outCount, int outStart, out int rowCount)
    {
        var col = new List<int>(Math.Max(layout.Count, outCount));
        for (int k = 0; k < layout.Count; k++)
        {
            col.Add(-1);
        }

        void Follow(Func<(int B, int L, int R), int> pick, int sourceStart, int outOffset)
        {
            for (int k = 0; k < layout.Count; k++)
            {
                int src = pick(layout[k]);
                if (src >= 0)
                {
                    col[k] = outStart + outOffset + (src - sourceStart);
                }
            }
        }

        void Sequential(int fromRow, int firstOut, int count)
        {
            int row = fromRow;
            for (int n = 0; n < count; n++, row++)
            {
                while (col.Count <= row)
                {
                    col.Add(-1);
                }

                col[row] = outStart + firstOut + n;
            }
        }

        int LastRowOf(Func<(int B, int L, int R), int> pick)
        {
            int last = -1;
            for (int k = 0; k < layout.Count; k++)
            {
                if (pick(layout[k]) >= 0)
                {
                    last = k;
                }
            }

            return last;
        }

        switch (choice)
        {
            case ResolutionChoice.Base:
                Follow(t => t.B, region.Base.Start, 0);
                break;
            case ResolutionChoice.Left:
                Follow(t => t.L, region.Left.Start, 0);
                break;
            case ResolutionChoice.Right:
                Follow(t => t.R, region.Right.Start, 0);
                break;
            case ResolutionChoice.LeftThenRight:
                Follow(t => t.L, region.Left.Start, 0);
                Sequential(LastRowOf(t => t.L) + 1, region.Left.Count, region.Right.Count);
                break;
            case ResolutionChoice.RightThenLeft:
                Follow(t => t.R, region.Right.Start, 0);
                Sequential(LastRowOf(t => t.R) + 1, region.Right.Count, region.Left.Count);
                break;
            case ResolutionChoice.Custom:
                Sequential(0, 0, outCount);
                break;
        }

        rowCount = col.Count;
        return [.. col];
    }

    private sealed record Operation(IReadOnlyList<(int Region, RegionResolution Before, RegionResolution After)> Changes);

    private sealed class BatchScope(MergeSession session) : IDisposable
    {
        private MergeSession? _session = session;

        public void Dispose() => Interlocked.Exchange(ref _session, null)?.EndBatch();
    }
}
