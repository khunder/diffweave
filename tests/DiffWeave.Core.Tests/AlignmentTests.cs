using System.Diagnostics;
using AwesomeAssertions;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Merge;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class AlignmentTests
{
    [Test]
    public void Two_way_rows_and_kinds()
    {
        string[] a = ["same", "old line one", "gone", "tail"];
        string[] b = ["same", "old line 1", "tail", "new"];
        TwoWayAlignment al = TwoWayAlignment.Create(a, b);
        MergeSessionTests.AssertAlignmentCoversAllLines(al, [a.Length, b.Length]);
        al.GetRowKind(0).Should().Be(RowKind.Equal);
        al.GetRowKind(1).Should().Be(RowKind.Changed);
        al.GetLine(1, TwoWayAlignment.PaneA).Should().Be(1);
        al.GetLine(1, TwoWayAlignment.PaneB).Should().Be(1);
        al.GetRowKind(2).Should().Be(RowKind.Removed);
        al.GetLine(2, TwoWayAlignment.PaneB).Should().Be(LineAlignment.Filler);
        al.GetRowKind(al.RowCount - 1).Should().Be(RowKind.Added);
        al.DifferenceGroups.Should().HaveCount(2);
        al.DifferenceGroups[0].RowCount.Should().Be(2);
        al.DifferenceGroups[1].Kind.Should().Be(RowKind.Added);
    }

    [Test]
    public void Similar_lines_are_paired_inside_hunks()
    {
        string[] a = ["int alpha = 1;", "string beta = \"x\";"];
        string[] b = ["// a brand new comment line", "int alpha = 2;", "string beta = \"y\";"];
        TwoWayAlignment al = TwoWayAlignment.Create(a, b);
        al.RowCount.Should().Be(3);
        al.GetLine(0, 0).Should().Be(LineAlignment.Filler);
        al.GetLine(1, 0).Should().Be(0);
        al.GetLine(1, 1).Should().Be(1);
    }

    [Test]
    public void Whitespace_only_rows()
    {
        string[] a = ["x = 1", "y"];
        string[] b = ["x  =  1", "", "y"];
        TwoWayAlignment al = TwoWayAlignment.Create(a, b);
        al.GetRowKind(0).Should().Be(RowKind.WhitespaceOnly);
        al.DifferenceGroups.Should().OnlyContain(g => g.IsWhitespaceOnly);
        al.FindNextGroup(-1, skipWhitespaceOnly: true).Should().BeNull();

        TwoWayAlignment ignoring = TwoWayAlignment.Create(a, b, new DiffOptions { Whitespace = WhitespaceMode.IgnoreAll, IgnoreBlankLines = true });
        ignoring.Diff.AreEqual.Should().BeTrue();
        ignoring.DifferenceGroups.Should().OnlyContain(g => g.IsWhitespaceOnly);
    }

    [Test]
    public void Group_navigation_and_line_mapping()
    {
        string[] a = ["1", "2", "3", "4", "5", "6", "7"];
        string[] b = ["1", "X", "3", "4", "5", "Y", "Z", "7"];
        TwoWayAlignment al = TwoWayAlignment.Create(a, b);
        DifferenceGroup first = al.FindNextGroup(-1)!.Value;
        first.StartRow.Should().Be(1);
        DifferenceGroup second = al.FindNextGroup(first.StartRow)!.Value;
        second.Index.Should().Be(1);
        al.FindNextGroup(second.StartRow).Should().BeNull();
        al.FindPreviousGroup(second.StartRow)!.Value.Index.Should().Be(0);
        al.FindPreviousGroup(second.StartRow + 1)!.Value.Index.Should().Be(0, "inside a group, previous means the group before it");
        al.FindPreviousGroup(first.StartRow).Should().BeNull();
        al.FindGroupAt(second.StartRow + 1)!.Value.Index.Should().Be(1);
        al.FindGroupAt(0).Should().BeNull();
        al.GetRow(1, 7).Should().Be(al.RowCount - 1);
        al.GetRow(0, 7).Should().Be(al.RowCount, "line == count maps past the end");
        int fillerRow = Enumerable.Range(0, al.RowCount).First(r => al.GetLine(r, 0) == LineAlignment.Filler);
        al.GetLine(fillerRow - 1, 0).Should().Be(5, "unmatched lines are zipped top-down, the filler comes last");
        al.GetNearestLine(fillerRow, 0).Should().Be(6);
    }

    [Test]
    public void Three_way_alignment_kinds()
    {
        string[] b = ["a", "b", "c", "d", "e"];
        string[] l = ["a", "B", "c", "d", "E1"];
        string[] r = ["a", "b", "c", "d", "E2"];
        ThreeWayAlignment al = ThreeWayAlignment.Create(b, l, r);
        MergeSessionTests.AssertAlignmentCoversAllLines(al, [5, 5, 5]);
        al.GetChange(1).Should().Be(ThreeWayChange.LeftChanged);
        al.GetRowKind(1).Should().Be(RowKind.Changed);
        al.GetRowKind(4).Should().Be(RowKind.Conflict);
        al.DifferenceGroups.Select(g => g.Kind).Should().Equal(RowKind.Changed, RowKind.Conflict);
        al.Chunks.Should().HaveCount(4);
    }

    [Test]
    public void Inline_word_diff()
    {
        InlineDiffResult r = InlineDiffer.Diff("int count = 10;", "int total = 10;");
        r.Left.Should().Equal(new TextRange(4, 5));
        r.Right.Should().Equal(new TextRange(4, 5));
        InlineDiffer.Diff("same", "same").IsEmpty.Should().BeTrue();
    }

    [Test]
    public void Inline_character_diff_and_options()
    {
        InlineDiffResult r = InlineDiffer.Diff("colour", "color", new InlineDiffOptions { Granularity = InlineGranularity.Character });
        r.Left.Should().Equal(new TextRange(4, 1));
        r.Right.Should().BeEmpty();

        InlineDiffer.Diff("a  b", "a b", new InlineDiffOptions { Whitespace = WhitespaceMode.IgnoreAmount }).IsEmpty.Should().BeTrue();
        InlineDiffer.Diff("a b  ", "a b", new InlineDiffOptions { Whitespace = WhitespaceMode.IgnoreTrailing }).IsEmpty.Should().BeTrue();
        InlineDiffer.Diff("Foo bar", "foo bar", new InlineDiffOptions { IgnoreCase = true }).IsEmpty.Should().BeTrue();
        InlineDiffer.Diff("a x b y c", "a X b Y c", new InlineDiffOptions { MergeGap = 3 }).Left.Should().Equal(new TextRange(2, 5));
    }

    [Test]
    public void Tokenizer_keeps_surrogate_pairs()
    {
        IReadOnlyList<TextRange> tokens = InlineDiffer.Tokenize("a😀b", InlineGranularity.Character);
        tokens.Should().Equal(new TextRange(0, 1), new TextRange(1, 2), new TextRange(3, 1));
        InlineDiffer.Tokenize("foo_bar(x, 12)", InlineGranularity.Word).Should().HaveCount(7);
    }

    [Test, CancelAfter(120_000)]
    public void Large_alignments_and_merges_are_fast()
    {
        var rng = new Random(5);
        string[] b = [.. Enumerable.Range(0, 200_000).Select(i => $"line {i}")];
        string[] l = (string[])b.Clone();
        string[] r = (string[])b.Clone();
        for (int k = 0; k < 1500; k++)
        {
            l[rng.Next(l.Length)] = "left " + k;
            r[rng.Next(r.Length)] = "right " + k;
        }

        var sw = Stopwatch.StartNew();
        TwoWayAlignment two = TwoWayAlignment.Create(b, l);
        two.RowCount.Should().Be(200_000);
        ThreeWayAlignment three = ThreeWayAlignment.Create(b, l, r);
        three.RowCount.Should().BeGreaterThanOrEqualTo(200_000);
        MergeSession session = MergeSession.Create(b, l, r);
        session.AutoResolve();
        MergeOutput output = session.BuildOutput();
        output.Alignment.RowCount.Should().BeGreaterThanOrEqualTo(200_000);
        session.Resolve(session.FindNextUnresolvedConflict(-1) is var c and >= 0 ? c : 0, ResolutionChoice.Left);
        session.BuildOutput();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }
}
