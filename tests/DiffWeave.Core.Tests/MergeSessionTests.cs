using AwesomeAssertions;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Merge;
using DiffWeave.Extensibility;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class MergeSessionTests
{
    private static readonly string[] s_base = ["1", "2", "3", "4", "5", "6", "7", "8", "9"];

    [Test]
    public void Classifies_regions()
    {
        string[] left = ["1", "2L", "3", "4", "5", "6S", "7", "8", "9L"];
        string[] right = ["1", "2", "3", "4R", "5", "6S", "7", "8", "9R"];
        MergeSession s = MergeSession.Create(s_base, left, right);
        s.Regions.Select(r => r.Kind).Should().Equal(
            RegionKind.Stable, RegionKind.LeftChanged, RegionKind.Stable, RegionKind.RightChanged, RegionKind.Stable,
            RegionKind.BothChangedSame, RegionKind.Stable, RegionKind.Conflict);
        s.ConflictCount.Should().Be(1);
        s.UnresolvedCount.Should().Be(4);
        s.Regions[7].Base.Should().Be(new LineRange(8, 1));
    }

    [Test]
    public void AutoResolve_takes_single_sided_and_identical_changes()
    {
        string[] left = ["1", "2L", "3", "4", "5", "6S", "7", "8", "9L"];
        string[] right = ["1", "2", "3", "4R", "5", "6S", "7", "8", "9R"];
        MergeSession s = MergeSession.Create(s_base, left, right);
        s.AutoResolve().Should().Be(3);
        s.UnresolvedConflictCount.Should().Be(1);
        s.BuildOutput().Lines.Should().Equal("1", "2L", "3", "4R", "5", "6S", "7", "8");

        s.Resolve(7, ResolutionChoice.LeftThenRight);
        s.UnresolvedCount.Should().Be(0);
        s.BuildOutput().Lines.Should().EndWith(["9L", "9R"]);
    }

    [Test]
    public void Touching_changes_conflict_like_git()
    {
        string[] left = ["1", "2L", "3", "4", "5", "6", "7", "8", "9"];
        string[] right = ["1", "2", "3R", "4", "5", "6", "7", "8", "9"];
        MergeSession.Create(s_base, left, right).ConflictCount.Should().Be(1);
    }

    [Test]
    public void Whitespace_only_conflicts_can_be_auto_solved()
    {
        string[] left = ["1", "2  x", "3"];
        string[] right = ["1", "2\tx", "3"];
        string[] b = ["1", "2", "3"];
        MergeSession s = MergeSession.Create(b, left, right);
        s.Regions[1].IsWhitespaceOnlyConflict.Should().BeTrue();
        s.AutoResolve().Should().Be(0);

        MergeSession s2 = MergeSession.Create(b, left, right, new MergeOptions { AutoSolveWhitespaceConflicts = true, WhitespaceConflictPreference = WhitespaceConflictPreference.Right });
        s2.AutoResolve().Should().Be(1);
        s2.BuildOutput().Lines.Should().Equal("1", "2\tx", "3");

        // Ignoring whitespace in the diff turns it into an identical change.
        MergeSession s3 = MergeSession.Create(b, left, right, new MergeOptions { Diff = new DiffOptions { Whitespace = WhitespaceMode.IgnoreAll } });
        s3.Regions[1].Kind.Should().Be(RegionKind.BothChangedSame);
    }

    [Test]
    public void Undo_redo_and_events()
    {
        string[] left = ["1", "L", "3"];
        string[] right = ["1", "R", "3"];
        string[] b = ["1", "2", "3"];
        MergeSession s = MergeSession.Create(b, left, right);
        var events = new List<MergeChangedEventArgs>();
        s.Changed += (_, e) => events.Add(e);

        s.Resolve(1, ResolutionChoice.Left);
        s.Resolve(1, ResolutionChoice.Right);
        s.BuildOutput().Lines.Should().Equal("1", "R", "3");
        s.Undo().Should().BeTrue();
        s.BuildOutput().Lines.Should().Equal("1", "L", "3");
        s.Undo().Should().BeTrue();
        s.GetResolution(1).IsResolved.Should().BeFalse();
        s.Undo().Should().BeFalse();
        s.Redo().Should().BeTrue();
        s.GetResolution(1).Choice.Should().Be(ResolutionChoice.Left);
        s.Resolve(1, ResolutionChoice.Base);
        s.CanRedo.Should().BeFalse("a new operation clears the redo stack");
        events.Select(e => e.Kind).Should().Equal(MergeChangeKind.Resolve, MergeChangeKind.Resolve, MergeChangeKind.Undo, MergeChangeKind.Undo, MergeChangeKind.Redo, MergeChangeKind.Resolve);
        events.Should().OnlyContain(e => e.Regions.SequenceEqual(new[] { 1 }));

        // Re-applying the same choice is a no-op.
        int before = events.Count;
        s.Resolve(1, ResolutionChoice.Base);
        events.Should().HaveCount(before);
    }

    [Test]
    public void Batches_are_one_undo_step()
    {
        string[] left = ["L1", "x", "L2"];
        string[] right = ["R1", "x", "R2"];
        string[] b = ["1", "x", "2"];
        MergeSession s = MergeSession.Create(b, left, right);
        int raised = 0;
        s.Changed += (_, _) => raised++;
        using (s.BeginBatch())
        {
            s.Resolve(0, ResolutionChoice.Left);
            using (s.BeginBatch())
            {
                s.Resolve(2, ResolutionChoice.Right);
            }
        }

        raised.Should().Be(1);
        s.UnresolvedCount.Should().Be(0);
        s.Undo();
        s.UnresolvedCount.Should().Be(2);
    }

    [Test]
    public void Manual_edits_map_back_to_regions()
    {
        string[] left = ["1", "L", "3", "4", "5"];
        string[] right = ["1", "R", "3", "4", "5"];
        string[] b = ["1", "2", "3", "4", "5"];
        MergeSession s = MergeSession.Create(b, left, right);
        s.BuildOutput().Lines.Should().Equal("1", "3", "4", "5");

        // Typing at the placeholder position of the unresolved conflict resolves it.
        s.ApplyManualEdit(1, 0, ["typed"]).Should().Equal(1);
        s.GetResolution(1).Choice.Should().Be(ResolutionChoice.Custom);
        s.BuildOutput().Lines.Should().Equal("1", "typed", "3", "4", "5");

        // Replacing lines across two regions: first gets the text, second becomes empty.
        s.ApplyManualEdit(1, 2, ["merged"]).Should().Equal(1, 2);
        s.BuildOutput().Lines.Should().Equal("1", "merged", "4", "5");
        s.GetCurrentLines(2).Should().BeEmpty();

        // Appending after the last line goes to the region owning the last line.
        s.ApplyManualEdit(4, 0, ["tail"]);
        s.BuildOutput().Lines.Should().EndWith(["5", "tail"]);

        s.Undo();
        s.Undo();
        s.BuildOutput().Lines.Should().Equal("1", "typed", "3", "4", "5");
    }

    [Test]
    public void Output_alignment_has_placeholders_and_all_lines()
    {
        string[] left = ["1", "L1", "L2", "3"];
        string[] right = ["1", "R", "3"];
        string[] b = ["1", "2", "3"];
        MergeSession s = MergeSession.Create(b, left, right);
        MergeAlignment al = s.BuildOutput().Alignment;
        al.PaneCount.Should().Be(4);
        int row = al.GetFirstRow(1);
        al.IsUnresolvedPlaceholder(row).Should().BeTrue();
        al.GetRowKind(row).Should().Be(RowKind.Conflict);
        al.DifferenceGroups.Should().ContainSingle(g => g.Kind == RowKind.Conflict);

        s.Resolve(1, ResolutionChoice.LeftThenRight);
        MergeOutput output = s.BuildOutput();
        output.Lines.Should().Equal("1", "L1", "L2", "R", "3");
        AssertAlignmentCoversAllLines(output.Alignment, [b.Length, left.Length, right.Length, output.Lines.Count]);
        output.GetOutputRange(1).Should().Be(new LineRange(1, 3));
        output.GetRegionOfLine(3).Should().Be(1);
        output.Alignment.IsUnresolvedPlaceholder(output.Alignment.GetFirstRow(1)).Should().BeFalse();
    }

    [Test]
    public void Exports_git_conflict_markers()
    {
        string[] left = ["1", "L", "3"];
        string[] right = ["1", "R", "3"];
        string[] b = ["1", "2", "3"];
        MergeSession s = MergeSession.Create(b, left, right);
        s.ExportWithConflictMarkers().Should().Equal("1", "<<<<<<< LOCAL", "L", "=======", "R", ">>>>>>> REMOTE", "3");
        s.ExportWithConflictMarkers(ConflictMarkerStyle.Diff3, new ConflictMarkerLabels("ours", "base", "theirs"))
            .Should().Equal("1", "<<<<<<< ours", "L", "||||||| base", "2", "=======", "R", ">>>>>>> theirs", "3");
    }

    [Test]
    public void Snapshot_and_proposals()
    {
        string[] left = ["1", "L", "3"];
        string[] right = ["1", "R", "3"];
        string[] b = ["1", "2", "3"];
        MergeSession s = MergeSession.Create(b, left, right);
        MergeSnapshot snap = s.CreateSnapshot("b.txt", "l.txt", "r.txt", "o.txt", "csharp");
        snap.Regions.Should().HaveCount(3);
        snap.Regions[1].Kind.Should().Be(MergeRegionKind.Conflict);
        snap.Regions[1].IsResolved.Should().BeFalse();
        snap.Regions[1].Output.Should().Be(new LineSpan(1, 0));
        snap.OutputLines.Should().Equal("1", "3");

        s.ApplyProposals([new ResolutionProposal(1, ["P"], "why", 0.5), new ResolutionProposal(99, ["X"], "bad", 1)]).Should().Be(1);
        s.BuildOutput().Lines.Should().Equal("1", "P", "3");
        s.Undo();
        s.UnresolvedConflictCount.Should().Be(1);
    }

    [Test]
    public void Navigation_between_unresolved_conflicts()
    {
        string[] left = ["L0", "1", "2", "L3", "4"];
        string[] right = ["R0", "1", "2", "R3", "4"];
        string[] b = ["0", "1", "2", "3", "4"];
        MergeSession s = MergeSession.Create(b, left, right);
        s.FindNextUnresolvedConflict(-1).Should().Be(0);
        s.FindNextUnresolvedConflict(0).Should().Be(2);
        s.FindPreviousUnresolvedConflict(2).Should().Be(0);
        s.Resolve(0, ResolutionChoice.Base);
        s.FindPreviousUnresolvedConflict(2).Should().Be(-1);
    }

    [Test]
    public void Empty_inputs()
    {
        MergeSession s = MergeSession.Create([], [], []);
        s.Regions.Should().BeEmpty();
        s.BuildOutput().Lines.Should().BeEmpty();
        MergeSession t = MergeSession.Create([], ["a"], ["b"]);
        t.ConflictCount.Should().Be(1);
        t.ApplyManualEdit(0, 0, ["x"]);
        t.BuildOutput().Lines.Should().Equal("x");
    }

    [Test]
    public void Randomized_merges_match_git_merge_file()
    {
        if (!TestUtil.GitAvailable)
        {
            Assert.Ignore("git is not available.");
        }

        using var tmp = new TempDir();
        var rng = new Random(2024);
        for (int iter = 0; iter < 60; iter++)
        {
            (string[] b, string[] l, string[] r, int conflicts) = GenerateMerge(rng, rng.Next(5, 80));
            string pb = TestUtil.WriteLines(tmp.Path, "base", b);
            string pl = TestUtil.WriteLines(tmp.Path, "left", l);
            string pr = TestUtil.WriteLines(tmp.Path, "right", r);
            (int code, string output, string err) = TestUtil.Run("git", ["merge-file", "-p", "-L", "LOCAL", "-L", "BASE", "-L", "REMOTE", pl, pb, pr], tmp.Path);
            code.Should().BeGreaterThanOrEqualTo(0, err);
            string[] gitLines = output.Length == 0 ? [] : output.TrimEnd('\n').Split('\n');

            MergeSession s = MergeSession.Create(b, l, r);
            s.AutoResolve();
            s.UnresolvedConflictCount.Should().Be(code, $"iteration {iter}: conflict count must match git");
            s.UnresolvedConflictCount.Should().Be(conflicts);
            if (code == 0)
            {
                s.BuildOutput().Lines.Should().Equal(gitLines, $"iteration {iter}: clean merges must be identical");
            }
            else
            {
                s.ExportWithConflictMarkers().Should().Equal(gitLines, $"iteration {iter}: conflict markers must be identical");
            }

            (_, string diff3, _) = TestUtil.Run("git", ["merge-file", "-p", "--diff3", "-L", "LOCAL", "-L", "BASE", "-L", "REMOTE", pl, pb, pr], tmp.Path);
            string[] diff3Lines = diff3.Length == 0 ? [] : diff3.TrimEnd('\n').Split('\n');
            s.ExportWithConflictMarkers(ConflictMarkerStyle.Diff3).Should().Equal(diff3Lines, $"iteration {iter}: diff3 style");
            AssertAlignmentCoversAllLines(s.BuildOutput().Alignment, [b.Length, l.Length, r.Length, s.BuildOutput().Lines.Count]);
        }
    }

    [Test]
    public void Randomized_overlapping_merges_are_consistent()
    {
        var rng = new Random(99);
        for (int iter = 0; iter < 300; iter++)
        {
            string[] b = TestUtil.RandomLines(rng, rng.Next(0, 30), 6);
            string[] l = TestUtil.Mutate(rng, b, rng.Next(0, 6), 6, "l");
            string[] r = TestUtil.Mutate(rng, b, rng.Next(0, 6), 6, "r");
            MergeSession s = MergeSession.Create(b, l, r);

            // Regions tile every input.
            int pb = 0, pl = 0, pr = 0;
            foreach (MergeRegion region in s.Regions)
            {
                region.Base.Start.Should().Be(pb);
                region.Left.Start.Should().Be(pl);
                region.Right.Start.Should().Be(pr);
                pb = region.Base.End;
                pl = region.Left.End;
                pr = region.Right.End;
            }

            (pb, pl, pr).Should().Be((b.Length, l.Length, r.Length));

            // Taking left everywhere yields left; right everywhere yields right.
            using (s.BeginBatch())
            {
                foreach (MergeRegion region in s.Regions.Where(x => x.IsChange))
                {
                    s.Resolve(region.Index, ResolutionChoice.Left);
                }
            }

            s.BuildOutput().Lines.Should().Equal(l);
            foreach (MergeRegion region in s.Regions.Where(x => x.IsChange))
            {
                s.Resolve(region.Index, ResolutionChoice.Right);
            }

            s.BuildOutput().Lines.Should().Equal(r);
            AssertAlignmentCoversAllLines(s.BuildOutput().Alignment, [b.Length, l.Length, r.Length, r.Length]);
            ThreeWayAlignment three = ThreeWayAlignment.Create(b, l, r);
            AssertAlignmentCoversAllLines(three, [b.Length, l.Length, r.Length]);
        }
    }

    internal static void AssertAlignmentCoversAllLines(LineAlignment al, int[] counts)
    {
        for (int p = 0; p < counts.Length; p++)
        {
            al.GetLineCount(p).Should().Be(counts[p]);
            int next = 0;
            for (int row = 0; row < al.RowCount; row++)
            {
                int line = al.GetLine(row, p);
                if (line >= 0)
                {
                    line.Should().Be(next++, $"pane {p} row {row}");
                    al.GetRow(p, line).Should().Be(row);
                }
            }

            next.Should().Be(counts[p]);
        }
    }

    /// <summary>
    /// Base of unique lines with well-separated edits; returns the expected number of conflicts. Edits are at least
    /// 4 unchanged lines apart because git joins conflicts separated by 3 lines or fewer into one.
    /// </summary>
    private static (string[] Base, string[] Left, string[] Right, int Conflicts) GenerateMerge(Random rng, int n)
    {
        string[] b = [.. Enumerable.Range(0, n).Select(i => $"base line {i}")];
        var left = new List<string>[n];
        var right = new List<string>[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = [b[i]];
            right[i] = [b[i]];
        }

        int conflicts = 0;
        for (int p = rng.Next(3); p < n; p += 5 + rng.Next(4))
        {
            List<string> Edit(string tag) => rng.Next(3) switch
            {
                0 => [],                                          // delete
                1 => [$"{tag} replaced {p}"],                     // replace
                _ => [$"{tag} replaced {p}", $"{tag} extra {p}"], // replace with two lines
            };

            switch (rng.Next(5))
            {
                case 0:
                    left[p] = Edit("left");
                    break;
                case 1:
                    right[p] = Edit("right");
                    break;
                case 2:
                    left[p] = Edit("same");
                    right[p] = [.. left[p]];
                    break;
                case 3:
                    left[p] = [$"left replaced {p}"];
                    right[p] = [$"right replaced {p}"];
                    conflicts++;
                    break;
                default:
                    break;
            }
        }

        return (b, [.. left.SelectMany(x => x)], [.. right.SelectMany(x => x)], conflicts);
    }
}
