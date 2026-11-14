using AwesomeAssertions;
using DiffWeave.Core.Directories;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class DirectoryComparerTests
{
    private static DirectoryComparisonNode Find(DirectoryComparisonResult r, string path)
        => r.Root.DescendantsAndSelf().Single(n => n.RelativePath == path);

    [Test]
    public async Task Two_way_statuses()
    {
        using var tmp = new TempDir();
        tmp.File("a/same.txt", "x");
        tmp.File("b/same.txt", "x");
        tmp.File("a/diff.txt", "one");
        tmp.File("b/diff.txt", "two");
        tmp.File("a/sub/onlya.txt", "1");
        tmp.File("b/sub/onlyb.txt", "1");
        tmp.File("a/kind", "file");
        tmp.Dir("b/kind");
        tmp.File("a/.git/config", "ignored");
        tmp.File("a/build/out.o", "ignored");

        var options = new DirectoryCompareOptions { IgnorePatterns = [".git", "*.o"] };
        DirectoryComparisonResult r = await DirectoryComparer.CompareAsync([Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")], options);
        Find(r, "same.txt").Status.Should().Be(EntryStatus.Identical);
        Find(r, "same.txt").EqualSides.Should().Be(SidesEqual.AB);
        Find(r, "diff.txt").Status.Should().Be(EntryStatus.Different);
        Find(r, "sub/onlya.txt").Status.Should().Be(EntryStatus.OnlyInA);
        Find(r, "sub/onlyb.txt").Status.Should().Be(EntryStatus.OnlyInB);
        Find(r, "sub").Status.Should().Be(EntryStatus.Different);
        Find(r, "kind").Status.Should().Be(EntryStatus.TypeMismatch);
        r.Root.DescendantsAndSelf().Should().NotContain(n => n.Name == ".git" || n.Name == "out.o");
        Find(r, "build").Status.Should().Be(EntryStatus.OnlyInA);
        r.Root.Status.Should().Be(EntryStatus.Different);
        r.Root.Children.First().IsDirectory.Should().BeTrue("directories are listed first");
        r.CountFilesByStatus()[EntryStatus.Identical].Should().Be(1);
    }

    [Test]
    public async Task Same_size_different_content_and_size_time_mode()
    {
        using var tmp = new TempDir();
        string fa = tmp.File("a/f", "abc");
        string fb = tmp.File("b/f", "abd");
        DateTime t = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fa, t);
        File.SetLastWriteTimeUtc(fb, t);
        string[] roots = [Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")];
        (await DirectoryComparer.CompareAsync(roots)).Root.Children[0].Status.Should().Be(EntryStatus.Different);
        (await DirectoryComparer.CompareAsync(roots, new DirectoryCompareOptions { Mode = DirectoryCompareMode.SizeAndTime }))
            .Root.Children[0].Status.Should().Be(EntryStatus.Identical, "size+time mode trusts metadata");
        (await DirectoryComparer.FilesEqualAsync(fa, fb)).Should().BeFalse();
    }

    [Test]
    public async Task Large_files_compare_by_content()
    {
        using var tmp = new TempDir();
        byte[] data = new byte[300_000];
        new Random(3).NextBytes(data);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "a"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "b"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "a", "big"), data);
        File.WriteAllBytes(Path.Combine(tmp.Path, "b", "big"), data);
        DirectoryComparisonResult r = await DirectoryComparer.CompareAsync([Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")]);
        r.Root.Children[0].Status.Should().Be(EntryStatus.Identical);
        data[299_999] ^= 1;
        File.WriteAllBytes(Path.Combine(tmp.Path, "b", "big"), data);
        r = await DirectoryComparer.CompareAsync([Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")]);
        r.Root.Children[0].Status.Should().Be(EntryStatus.Different);
    }

    [Test]
    public async Task Three_way_statuses()
    {
        using var tmp = new TempDir();
        foreach (string side in new[] { "a", "b", "c" })
        {
            tmp.File($"{side}/all.txt", "same");
        }

        tmp.File("a/ab.txt", "1");
        tmp.File("b/ab.txt", "1");
        tmp.File("c/onlyc.txt", "1");
        tmp.File("a/m.txt", "base");
        tmp.File("b/m.txt", "base");
        tmp.File("c/m.txt", "changed");

        DirectoryComparisonResult r = await DirectoryComparer.CompareAsync([Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b"), Path.Combine(tmp.Path, "c")]);
        Find(r, "all.txt").Status.Should().Be(EntryStatus.Identical);
        Find(r, "ab.txt").Status.Should().Be(EntryStatus.MissingInC);
        Find(r, "onlyc.txt").Status.Should().Be(EntryStatus.OnlyInC);
        DirectoryComparisonNode m = Find(r, "m.txt");
        m.Status.Should().Be(EntryStatus.Different);
        m.EqualSides.Should().Be(SidesEqual.AB);
    }

    [Test]
    public async Task Symlinks_compare_targets()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("Symlink creation needs privileges on Windows.");
        }

        using var tmp = new TempDir();
        tmp.File("a/t.txt", "x");
        tmp.File("b/t.txt", "x");
        File.CreateSymbolicLink(Path.Combine(tmp.Path, "a", "link"), "t.txt");
        File.CreateSymbolicLink(Path.Combine(tmp.Path, "b", "link"), "other.txt");
        Directory.CreateSymbolicLink(Path.Combine(tmp.Path, "a", "loop"), Path.Combine(tmp.Path, "a"));
        string[] roots = [Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")];

        DirectoryComparisonResult r = await DirectoryComparer.CompareAsync(roots);
        Find(r, "link").Status.Should().Be(EntryStatus.Different);
        Find(r, "link").Sides[0].Kind.Should().Be(EntryKind.Symlink);
        Find(r, "loop").Status.Should().Be(EntryStatus.OnlyInA);

        DirectoryComparisonResult skipped = await DirectoryComparer.CompareAsync(roots, new DirectoryCompareOptions { Symlinks = SymlinkHandling.Skip });
        skipped.Root.Children.Should().ContainSingle(n => n.Name == "t.txt");

        // Following links: the self-referencing directory link is detected as a cycle instead of recursing forever.
        DirectoryComparisonResult followed = await DirectoryComparer.CompareAsync(roots, new DirectoryCompareOptions { Symlinks = SymlinkHandling.Follow });
        Find(followed, "loop").Status.Should().Be(EntryStatus.Error);
        Find(followed, "link").Sides[1].Kind.Should().Be(EntryKind.Other, "dangling link");
    }

    [Test]
    public async Task Cancellation_and_progress()
    {
        using var tmp = new TempDir();
        for (int i = 0; i < 300; i++)
        {
            tmp.File($"a/f{i}.txt", "x" + i);
            tmp.File($"b/f{i}.txt", "x" + i);
        }

        string[] roots = [Path.Combine(tmp.Path, "a"), Path.Combine(tmp.Path, "b")];
        var reports = new List<DirectoryCompareProgress>();
        var progress = new SyncProgress(reports.Add);
        DirectoryComparisonResult r = await DirectoryComparer.CompareAsync(roots, null, progress);
        r.Root.Status.Should().Be(EntryStatus.Identical);
        reports.Should().NotBeEmpty();
        reports[^1].FilesCompared.Should().Be(300);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => DirectoryComparer.CompareAsync(roots, null, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void Glob_patterns()
    {
        DirectoryComparer.GlobToRegex("*.o", false).IsMatch("x.o").Should().BeTrue();
        DirectoryComparer.GlobToRegex("*.o", false).IsMatch("d/x.o").Should().BeFalse();
        DirectoryComparer.GlobToRegex("build/**", false).IsMatch("build/a/b.c").Should().BeTrue();
        DirectoryComparer.GlobToRegex("**/obj", false).IsMatch("src/x/obj").Should().BeTrue();
        DirectoryComparer.GlobToRegex("**/obj", false).IsMatch("obj").Should().BeTrue();
        DirectoryComparer.GlobToRegex("f?le", true).IsMatch("FILE").Should().BeTrue();
    }

    private sealed class SyncProgress(Action<DirectoryCompareProgress> report) : IProgress<DirectoryCompareProgress>
    {
        private readonly Lock _gate = new();

        public void Report(DirectoryCompareProgress value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
