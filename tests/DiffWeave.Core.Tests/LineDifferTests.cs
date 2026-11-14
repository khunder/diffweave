using System.Diagnostics;
using AwesomeAssertions;
using DiffWeave.Core.Diff;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class LineDifferTests
{
    private static readonly DiffAlgorithm[] s_algorithms = [DiffAlgorithm.Myers, DiffAlgorithm.Patience, DiffAlgorithm.Histogram];

    [Test]
    public void Identical_inputs_have_no_hunks([Values] DiffAlgorithm algorithm)
    {
        string[] a = ["a", "b", "c"];
        DiffResult r = LineDiffer.Diff(a, [.. a], new DiffOptions { Algorithm = algorithm });
        r.Hunks.Should().BeEmpty();
        r.AreEqual.Should().BeTrue();
    }

    [Test]
    public void Simple_change_insert_delete([Values] DiffAlgorithm algorithm)
    {
        string[] a = ["a", "b", "c", "d", "e"];
        string[] b = ["a", "B", "c", "e", "f"];
        DiffResult r = LineDiffer.Diff(a, b, new DiffOptions { Algorithm = algorithm });
        r.Hunks.Should().Equal(
            new DiffHunk(1, 1, 1, 1),
            new DiffHunk(3, 1, 3, 0),
            new DiffHunk(5, 0, 4, 1));
    }

    [Test]
    public void Empty_sides()
    {
        LineDiffer.Diff([], ["x", "y"]).Hunks.Should().Equal(new DiffHunk(0, 0, 0, 2));
        LineDiffer.Diff(["x", "y"], []).Hunks.Should().Equal(new DiffHunk(0, 2, 0, 0));
        LineDiffer.Diff([], []).Hunks.Should().BeEmpty();
    }

    [Test]
    public void Whitespace_and_case_options()
    {
        string[] a = ["int  x = 1;  ", "Foo", "\tbar"];
        string[] b = ["int x = 1;", "foo", "bar"];
        LineDiffer.Diff(a, b).Hunks.Should().HaveCount(1);
        LineDiffer.Diff(a, b, new DiffOptions { Whitespace = WhitespaceMode.IgnoreTrailing }).EditCount.Should().Be(6);
        LineDiffer.Diff(a, b, new DiffOptions { Whitespace = WhitespaceMode.IgnoreAmount, IgnoreCase = true }).Hunks
            .Should().Equal(new DiffHunk(2, 1, 2, 1)); // "\tbar" vs "bar": leading whitespace exists only on one side
        LineDiffer.Diff(a, b, new DiffOptions { Whitespace = WhitespaceMode.IgnoreAll, IgnoreCase = true }).Hunks.Should().BeEmpty();
    }

    [Test]
    public void Ignore_blank_lines_flags_hunks()
    {
        string[] a = ["a", "b", "c"];
        string[] b = ["a", "", "  ", "b", "C"];
        DiffResult r = LineDiffer.Diff(a, b, new DiffOptions { IgnoreBlankLines = true });
        r.Hunks.Should().HaveCount(2);
        r.Hunks[0].IsIgnorable.Should().BeTrue();
        r.Hunks[1].IsIgnorable.Should().BeFalse();
        r.AreEqual.Should().BeFalse();
    }

    [Test]
    public void Insertions_are_slid_down_to_a_canonical_position()
    {
        // Inserting a duplicate of an existing block is ambiguous; the hunk is placed as low as possible.
        string[] a = ["}", "", "x"];
        string[] b = ["}", "", "}", "", "x"];
        DiffResult r = LineDiffer.Diff(a, b);
        r.Hunks.Should().Equal(new DiffHunk(2, 0, 2, 2));
    }

    [Test]
    public void Randomized_edit_scripts_are_valid_and_myers_is_minimal()
    {
        var rng = new Random(12345);
        for (int iter = 0; iter < 400; iter++)
        {
            string[] a = TestUtil.RandomLines(rng, rng.Next(0, 40), rng.Next(2, 12));
            string[] b = rng.Next(3) == 0 ? TestUtil.RandomLines(rng, rng.Next(0, 40), 8) : TestUtil.Mutate(rng, a, rng.Next(0, 12), 8, "l");
            int reference = TestUtil.ReferenceEditDistance(a, b);
            foreach (DiffAlgorithm alg in s_algorithms)
            {
                foreach (bool minimal in new[] { true, false })
                {
                    DiffResult r = LineDiffer.Diff(a, b, new DiffOptions { Algorithm = alg, Minimal = minimal });
                    TestUtil.Apply(a, b, r).Should().Equal(b, $"iteration {iter}, {alg}");
                    AssertWellFormed(r, a, b);
                    if (alg == DiffAlgorithm.Myers)
                    {
                        r.EditCount.Should().Be(reference, $"Myers must be minimal (iteration {iter})");
                    }
                    else
                    {
                        r.EditCount.Should().BeGreaterThanOrEqualTo(reference);
                    }
                }
            }
        }
    }

    [Test]
    public void Matches_git_minimal_edit_count()
    {
        if (!TestUtil.GitAvailable)
        {
            Assert.Ignore("git is not available.");
        }

        using var tmp = new TempDir();
        var rng = new Random(777);
        for (int iter = 0; iter < 40; iter++)
        {
            string[] a = TestUtil.RandomLines(rng, rng.Next(1, 300), rng.Next(3, 40));
            string[] b = TestUtil.Mutate(rng, a, rng.Next(1, 60), 40, "l");
            string pa = TestUtil.WriteLines(tmp.Path, "a.txt", a);
            string pb = TestUtil.WriteLines(tmp.Path, "b.txt", b);
            (int code, string output, string err) = TestUtil.Run("git", ["diff", "--no-index", "--no-color", "--minimal", "-U0", pa, pb], tmp.Path);
            code.Should().BeOneOf([0, 1], err);
            int gitEdits = CountGitEdits(output);
            DiffResult ours = LineDiffer.Diff(a, b, new DiffOptions { Minimal = true });
            ours.EditCount.Should().Be(gitEdits, $"iteration {iter}");
            TestUtil.Apply(a, b, ours).Should().Equal(b);
        }
    }

    [Test]
    public void Patience_and_histogram_agree_with_git_on_validity()
    {
        if (!TestUtil.GitAvailable)
        {
            Assert.Ignore("git is not available.");
        }

        using var tmp = new TempDir();
        var rng = new Random(4242);
        for (int iter = 0; iter < 20; iter++)
        {
            string[] a = TestUtil.RandomLines(rng, rng.Next(1, 200), rng.Next(20, 200));
            string[] b = TestUtil.Mutate(rng, a, rng.Next(1, 30), 200, "l");
            string pa = TestUtil.WriteLines(tmp.Path, "a.txt", a);
            string pb = TestUtil.WriteLines(tmp.Path, "b.txt", b);
            foreach ((DiffAlgorithm alg, string flag) in new[] { (DiffAlgorithm.Patience, "--patience"), (DiffAlgorithm.Histogram, "--histogram") })
            {
                (_, string output, _) = TestUtil.Run("git", ["diff", "--no-index", "--no-color", flag, "-U0", pa, pb], tmp.Path);
                int gitEdits = CountGitEdits(output);
                DiffResult ours = LineDiffer.Diff(a, b, new DiffOptions { Algorithm = alg });
                TestUtil.Apply(a, b, ours).Should().Equal(b);

                // Different anchor choices are allowed, but results must stay in the same ballpark as git's.
                ours.EditCount.Should().BeLessThanOrEqualTo((int)(gitEdits * 1.5) + 4, $"{alg} iteration {iter}");
            }
        }
    }

    [Test]
    public void Cancellation_is_honoured()
    {
        string[] a = [.. Enumerable.Range(0, 50_000).Select(i => "a" + (i % 1000))];
        string[] b = [.. Enumerable.Range(0, 50_000).Select(i => "a" + ((i * 7) % 1000))];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Action act = () => LineDiffer.Diff(a, b, new DiffOptions { Minimal = true }, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Test, CancelAfter(60_000)]
    public void Large_files_are_fast()
    {
        // 200k lines with scattered edits.
        var rng = new Random(1);
        string[] a = [.. Enumerable.Range(0, 200_000).Select(i => $"line {i} {rng.Next(1000)}")];
        string[] b = (string[])a.Clone();
        var list = b.ToList();
        for (int k = 0; k < 2000; k++)
        {
            int pos = rng.Next(list.Count);
            if (k % 3 == 0)
            {
                list.RemoveAt(pos);
            }
            else if (k % 3 == 1)
            {
                list.Insert(pos, "inserted " + k);
            }
            else
            {
                list[pos] = "changed " + k;
            }
        }

        b = [.. list];
        foreach (DiffAlgorithm alg in s_algorithms)
        {
            var sw = Stopwatch.StartNew();
            DiffResult r = LineDiffer.Diff(a, b, new DiffOptions { Algorithm = alg });
            sw.Stop();
            TestUtil.Apply(a, b, r).Should().Equal(b);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), $"{alg} took {sw.Elapsed}");
        }

        // Completely different files: the unique-line preprocessing must make this trivial.
        string[] c = [.. Enumerable.Range(0, 200_000).Select(i => $"other {i}")];
        var sw2 = Stopwatch.StartNew();
        LineDiffer.Diff(a, c).EditCount.Should().Be(400_000);
        sw2.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Test, CancelAfter(60_000)]
    public void Heavily_shuffled_large_input_stays_bounded()
    {
        // Many repeated lines in different order: worst case for O(ND); the cost heuristic must keep it bounded.
        var rng = new Random(2);
        string[] a = [.. Enumerable.Range(0, 60_000).Select(_ => "v" + rng.Next(50))];
        string[] b = [.. Enumerable.Range(0, 60_000).Select(_ => "v" + rng.Next(50))];
        var sw = Stopwatch.StartNew();
        DiffResult r = LineDiffer.Diff(a, b);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        TestUtil.Apply(a, b, r).Should().Equal(b);
    }

    private static void AssertWellFormed(DiffResult r, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int lastA = -1, lastB = -1;
        foreach (DiffHunk h in r.Hunks)
        {
            (h.CountA + h.CountB).Should().BePositive();
            h.StartA.Should().BeGreaterThan(lastA);
            h.StartB.Should().BeGreaterThan(lastB);
            h.EndA.Should().BeLessThanOrEqualTo(a.Count);
            h.EndB.Should().BeLessThanOrEqualTo(b.Count);
            (h.StartA - Math.Max(lastA, 0)).Should().Be(h.StartB - Math.Max(lastB, 0), "unchanged stretches must have equal length");
            lastA = h.EndA;
            lastB = h.EndB;
        }
    }

    private static int CountGitEdits(string output)
    {
        int count = 0;
        bool inHunks = false;
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunks = true;
                continue;
            }

            if (inHunks && line.Length > 0 && (line[0] == '+' || line[0] == '-'))
            {
                count++;
            }
        }

        return count;
    }
}
