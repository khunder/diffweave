using DiffWeave.App.Cli;

namespace DiffWeave.App.Tests.Cli;

public class CommandLineParserTests
{
    private static CommandLineOptions Parse(params string[] args)
    {
        ParseResult r = CommandLineParser.Parse(args, p => p.StartsWith("dir", StringComparison.Ordinal));
        r.Error.Should().BeNull();
        return r.Options!;
    }

    private static string Fail(params string[] args)
    {
        ParseResult r = CommandLineParser.Parse(args, p => p.StartsWith("dir", StringComparison.Ordinal));
        r.Success.Should().BeFalse();
        return r.Error!;
    }

    [Test]
    public void No_arguments_open_an_empty_window() => Parse().Mode.Should().Be(LaunchMode.Empty);

    [Test]
    public void Two_files_are_a_diff()
    {
        CommandLineOptions o = Parse("a.txt", "b.txt");
        o.Mode.Should().Be(LaunchMode.FileDiff);
        o.Inputs.Should().Equal("a.txt", "b.txt");
    }

    [Test]
    public void One_file_is_a_diff_against_an_empty_pane() => Parse("a.txt").Mode.Should().Be(LaunchMode.FileDiff);

    [Test]
    public void Three_files_with_output_are_a_merge()
    {
        CommandLineOptions o = Parse("base", "local", "remote", "-o", "out");
        o.Mode.Should().Be(LaunchMode.FileMerge);
        o.Output.Should().Be("out");
        o.Merge.Should().BeTrue();
    }

    [Test]
    public void Git_kdiff3_style_invocation_is_understood()
    {
        CommandLineOptions o = Parse("--auto", "--L1", "f (Base)", "--L2", "f (Local)", "--L3", "f (Remote)", "-o", "f", "f.BASE", "f.LOCAL", "f.REMOTE");
        o.Auto.Should().BeTrue();
        o.Labels.Should().Equal("f (Base)", "f (Local)", "f (Remote)");
        o.Inputs.Should().Equal("f.BASE", "f.LOCAL", "f.REMOTE");
        o.Mode.Should().Be(LaunchMode.FileMerge);
    }

    [Test]
    public void Base_option_goes_first()
    {
        CommandLineOptions o = Parse("-b", "base", "local", "remote", "--merge", "--output=out");
        o.Inputs.Should().Equal("base", "local", "remote");
        o.Output.Should().Be("out");
    }

    [Test]
    public void Fname_labels_fill_free_slots_in_order()
    {
        CommandLineOptions o = Parse("--L2", "two", "--fname", "one", "--fname", "three", "a", "b", "c");
        o.Labels.Should().Equal("one", "two", "three");
    }

    [Test]
    public void Directories_are_compared()
    {
        CommandLineOptions o = Parse("dirA", "dirB", "dirC");
        o.Mode.Should().Be(LaunchMode.DirectoryDiff);
        o.Inputs.Should().HaveCount(3);
    }

    [Test]
    public void Help_version_and_quiet()
    {
        Parse("-h").ShowHelp.Should().BeTrue();
        Parse("--help").ShowHelp.Should().BeTrue();
        Parse("--version").ShowVersion.Should().BeTrue();
        Parse("--qall", "a", "b", "c", "-o", "o", "--auto").Quiet.Should().BeTrue();
    }

    [Test]
    public void Test_resolve_hook()
    {
        Parse("a", "b", "c", "-o", "o", "--test-resolve-all", "b").TestResolveAll.Should().Be("B");
        Fail("a", "b", "c", "-o", "o", "--test-resolve-all", "x").Should().Contain("test-resolve-all");
    }

    [Test]
    public void Errors()
    {
        Fail("--bogus").Should().Contain("Unknown option");
        Fail("a", "b", "c", "d").Should().Contain("three");
        Fail("dirA", "file").Should().Contain("directory");
        Fail("-o").Should().Contain("requires");
        Fail("--auto", "--merge", "a", "b").Should().Contain("--auto");
        Fail("--merge", "a").Should().Contain("two");
    }

    [Test]
    public void Auto_without_merge_is_ignored_with_a_warning()
    {
        CommandLineOptions o = Parse("--auto", "a", "b");
        o.Auto.Should().BeFalse();
        o.Warnings.Should().ContainSingle();
    }

    [Test]
    public void Usage_mentions_every_option()
    {
        foreach (string opt in new[] { "--merge", "--output", "--base", "--L1", "--fname", "--auto", "--qall", "--help", "--version" })
        {
            CommandLineParser.Usage.Should().Contain(opt);
        }
    }
}
