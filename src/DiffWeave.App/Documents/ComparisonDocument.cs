using DiffWeave.App.Editor;
using DiffWeave.Core.Diff;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Documents;

/// <summary>A read-only comparison of two (A | B) or three (A=base | B | C) files.</summary>
public sealed class ComparisonDocument : DiffDocument
{
    private readonly ReadOnlyTextSource[] _sources;
    private readonly AlignmentDecorationSource[] _decorations;

    /// <summary>Creates the document; call <see cref="RecomputeAsync"/> to compute the diff.</summary>
    public ComparisonDocument(IReadOnlyList<InputFile> inputs)
    {
        if (inputs.Count is < 2 or > 3)
        {
            throw new ArgumentException("Two or three inputs are required.", nameof(inputs));
        }

        Inputs = inputs;
        _sources = [.. inputs.Select(i => new ReadOnlyTextSource(i.Lines))];
        _decorations = new AlignmentDecorationSource[inputs.Count];
        List<PaneModel> panes = [];
        string[] badges = inputs.Count == 2 ? ["A", "B"] : ["A", "B", "C"];
        string[] keys = inputs.Count == 2 ? ["DW.Badge.B", "DW.Badge.C"] : ["DW.Badge.A", "DW.Badge.B", "DW.Badge.C"];
        PaneRole[] roles = inputs.Count == 2 ? [PaneRole.Left, PaneRole.Right] : [PaneRole.Base, PaneRole.Left, PaneRole.Right];
        for (int i = 0; i < inputs.Count; i++)
        {
            PaneModel p = new(i, roles[i], badges[i], keys[i], _sources[i], inputs[i]);
            DescribePane(p, inputs[i]);
            _decorations[i] = new AlignmentDecorationSource(() => Mapping, i, Partner, pane => _sources[pane]);
            p.DiffDecorations = _decorations[i];
            panes.Add(p);
        }

        Panes = panes;
    }

    /// <inheritdoc />
    public override EditorKind Kind => Inputs.Count == 2 ? EditorKind.TwoWayDiff : EditorKind.ThreeWayDiff;

    /// <summary>True when the files are equal under the current options.</summary>
    public bool AreEqual => Mapping is { } m && m.Blocks.All(b => b.IsWhitespaceOnly && Options.Whitespace != WhitespaceMode.None);

    private int? Partner(int row, int pane)
    {
        if (Inputs.Count == 2)
        {
            return 1 - pane;
        }

        if (pane != 0)
        {
            return 0;
        }

        LineAlignment a = Mapping!.Alignment;
        int left = a.GetLine(row, 1);
        return left >= 0 && a.GetLine(row, 0) >= 0 && !string.Equals(_sources[1].GetLine(left), _sources[0].GetLine(a.GetLine(row, 0)), StringComparison.Ordinal) ? 1 : 2;
    }

    /// <inheritdoc />
    public override async Task RecomputeAsync(CancellationToken cancellationToken = default)
    {
        DiffOptions options = Options;
        IReadOnlyList<string>[] lines = [.. Inputs.Select(i => i.Lines)];
        PaneMapping mapping = await Task.Run(
            () =>
            {
                if (lines.Length == 2)
                {
                    TwoWayAlignment a = TwoWayAlignment.Create(lines[0], lines[1], options, cancellationToken);
                    return new PaneMapping(a, (g, _) => ClassifyTwoWay(a, g));
                }

                ThreeWayAlignment t = ThreeWayAlignment.Create(lines[0], lines[1], lines[2], options, cancellationToken);
                return new PaneMapping(t, (g, _) => ClassifyThreeWay(t, g));
            },
            cancellationToken).ConfigureAwait(true);
        Mapping = mapping;
        foreach (AlignmentDecorationSource d in _decorations)
        {
            d.Invalidate(options);
        }

        OnMappingChanged();
        OnStateChanged();
    }

    private static (HighlightKind, IReadOnlyList<HighlightKind>?) ClassifyTwoWay(TwoWayAlignment a, DifferenceGroup g)
    {
        bool added = true, removed = true;
        for (int r = g.StartRow; r < g.EndRow; r++)
        {
            RowKind k = a.GetRowKind(r);
            added &= k == RowKind.Added;
            removed &= k == RowKind.Removed;
        }

        if (added)
        {
            return (HighlightKind.Inserted, [HighlightKind.Inserted, HighlightKind.Inserted]);
        }

        if (removed)
        {
            return (HighlightKind.Deleted, [HighlightKind.Deleted, HighlightKind.Deleted]);
        }

        return (HighlightKind.Changed, null);
    }

    private static (HighlightKind, IReadOnlyList<HighlightKind>?) ClassifyThreeWay(ThreeWayAlignment a, DifferenceGroup g)
    {
        bool left = false, right = false, conflict = false;
        for (int r = g.StartRow; r < g.EndRow; r++)
        {
            switch (a.GetChange(r))
            {
                case ThreeWayChange.LeftChanged: left = true; break;
                case ThreeWayChange.RightChanged: right = true; break;
                case ThreeWayChange.BothChangedSame: left = right = true; break;
                case ThreeWayChange.Conflict: conflict = true; break;
            }
        }

        if (conflict)
        {
            return (HighlightKind.Conflict, [HighlightKind.Conflict, HighlightKind.Conflict, HighlightKind.Conflict]);
        }

        HighlightKind main = left && !right ? HighlightKind.SourceB : right && !left ? HighlightKind.SourceC : HighlightKind.Changed;
        return (main, [HighlightKind.SourceA, left ? HighlightKind.SourceB : HighlightKind.None, right ? HighlightKind.SourceC : HighlightKind.None]);
    }
}
