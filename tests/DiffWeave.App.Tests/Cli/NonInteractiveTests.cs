using DiffWeave.App.Cli;
using DiffWeave.App.Services;

namespace DiffWeave.App.Tests.Cli;

public class NonInteractiveTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() => _dir = TestUtil.CreateTempDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private int? Run(params string[] args)
    {
        CommandLineOptions o = CommandLineParser.Parse(args).Options!;
        return NonInteractive.TryRun(o, TextWriter.Null, new AppSettings());
    }

    [Test]
    public void Auto_merges_non_conflicting_changes_and_exits_0()
    {
        string b = Write("base", "a\nb\nc\nd\ne\n");
        string l = Write("left", "A\nb\nc\nd\ne\n");
        string r = Write("right", "a\nb\nc\nd\nE\n");
        string o = Path.Combine(_dir, "out");
        Run("--auto", "-o", o, b, l, r).Should().Be(ExitCodes.Success);
        File.ReadAllText(o).Should().Be("A\nb\nc\nd\nE\n");
    }

    [Test]
    public void Auto_with_conflicts_opens_the_ui_or_fails_when_quiet()
    {
        (string b, string l, string r) = TestData.WriteMergeInputs(_dir);
        string o = Path.Combine(_dir, "out.cs");
        Run("--auto", "-o", o, b, l, r).Should().BeNull();
        Run("--auto", "--qall", "-o", o, b, l, r).Should().Be(ExitCodes.Cancelled);
        File.Exists(o).Should().BeFalse();
    }

    [Test]
    public void Resolve_all_hook_saves_the_chosen_side()
    {
        (string b, string l, string r) = TestData.WriteMergeInputs(_dir);
        string o = Path.Combine(_dir, "out.cs");
        Run("--auto", "--test-resolve-all", "C", "-o", o, b, l, r).Should().Be(ExitCodes.Success);
        string result = File.ReadAllText(o);
        result.Should().Contain("Stock with {Count} items").And.Contain("ArgumentException(\"Invalid quantity\"").And.Contain("Total =>");
        result.Should().NotContain("<<<<<<<");
    }

    [Test]
    public void Crlf_and_bom_are_preserved()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        string b = WriteBytes("base", [.. bom, .. "one\r\ntwo\r\n"u8.ToArray()]);
        string l = WriteBytes("left", [.. bom, .. "ONE\r\ntwo\r\n"u8.ToArray()]);
        string r = WriteBytes("right", [.. bom, .. "one\r\ntwo\r\nthree\r\n"u8.ToArray()]);
        string o = Path.Combine(_dir, "out");
        Run("--auto", "-o", o, b, l, r).Should().Be(ExitCodes.Success);
        File.ReadAllBytes(o).Should().Equal([.. bom, .. "ONE\r\ntwo\r\nthree\r\n"u8.ToArray()]);
    }

    [Test]
    public void Diffs_are_never_handled_non_interactively()
        => Run("a", "b").Should().BeNull();

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
