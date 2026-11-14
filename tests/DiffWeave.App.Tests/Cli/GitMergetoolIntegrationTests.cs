using System.Diagnostics;

namespace DiffWeave.App.Tests.Cli;

/// <summary>
/// Creates a real conflicted git merge in a temporary repository and runs <c>git mergetool --tool=diffweave</c>
/// with the configuration documented in docs/git-integration.md, using the app's non-interactive path.
/// </summary>
[NonParallelizable]
public class GitMergetoolIntegrationTests
{
    private string _repo = null!;

    private static string AppExecutable
    {
        get
        {
            string config = Path.GetFileName(TestContext.CurrentContext.TestDirectory.TrimEnd(Path.DirectorySeparatorChar));
            string bin = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "DiffWeave.App", config));
            string exe = Path.Combine(bin, OperatingSystem.IsWindows() ? "diffweave.exe" : "diffweave");
            return File.Exists(exe) ? exe : Path.Combine(TestContext.CurrentContext.TestDirectory, OperatingSystem.IsWindows() ? "diffweave.exe" : "diffweave");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _repo = TestUtil.CreateTempDirectory();
        Git("init -q -b main");
        Git("config user.email test@example.com");
        Git("config user.name Test");
        Git("config commit.gpgsign false");
        File.WriteAllText(Path.Combine(_repo, "Stock.cs"), TestData.Base);
        Git("add Stock.cs");
        Git("commit -q -m base");
        Git("checkout -q -b feature");
        File.WriteAllText(Path.Combine(_repo, "Stock.cs"), TestData.Right);
        Git("commit -q -am remote");
        Git("checkout -q main");
        File.WriteAllText(Path.Combine(_repo, "Stock.cs"), TestData.Left);
        Git("commit -q -am local");
        (int code, _) = Git("merge feature", allowFailure: true);
        code.Should().NotBe(0, "the merge must conflict");
        string tool = AppExecutable;
        File.Exists(tool).Should().BeTrue($"the app must be built at {tool}");
        Git("config merge.tool diffweave");
        GitArgs(["config", "mergetool.diffweave.path", tool]);
        Git("config mergetool.diffweave.trustExitCode true");
        Git("config mergetool.keepBackup false");
        Git("config mergetool.prompt false");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_repo, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The documented mergetool command (see docs/git-integration.md) plus test-only options.</summary>
    private void ConfigureCmd(string extraArgs)
        => GitArgs(["config", "mergetool.diffweave.cmd",
            $"'{AppExecutable}' --auto {extraArgs} --L1 \"$MERGED (Base)\" --L2 \"$MERGED (Local)\" --L3 \"$MERGED (Remote)\" -o \"$MERGED\" \"$BASE\" \"$LOCAL\" \"$REMOTE\""]);

    [Test]
    public void Mergetool_resolves_the_conflict_and_git_accepts_the_result()
    {
        ConfigureCmd("--test-resolve-all B");
        (int code, string output) = Git("mergetool --tool=diffweave", allowFailure: true);
        code.Should().Be(0, output);
        Git("diff --name-only --diff-filter=U").Output.Trim().Should().BeEmpty("the conflict is marked resolved");
        string merged = File.ReadAllText(Path.Combine(_repo, "Stock.cs"));
        merged.Should().Contain("public int Total").And.Contain("StringComparer.OrdinalIgnoreCase").And.NotContain("<<<<<<<");
        merged.Should().Contain("return _items.Remove(name);", "non-conflicting remote changes are merged automatically");
        (int commitCode, _) = Git("commit -q -m merged", allowFailure: true);
        commitCode.Should().Be(0);
    }

    [Test]
    public void Unresolved_conflicts_exit_1_and_git_keeps_the_file_conflicted()
    {
        ConfigureCmd("--qall");
        (int code, string output) = Git("mergetool --tool=diffweave", allowFailure: true);
        code.Should().NotBe(0, output);
        Git("diff --name-only --diff-filter=U").Output.Trim().Should().Be("Stock.cs");
    }

    [Test]
    public void Exit_codes_of_the_executable()
    {
        Run(AppExecutable, "--version").Code.Should().Be(0);
        Run(AppExecutable, "--help").Output.Should().Contain("--auto");
        Run(AppExecutable, "--no-such-option").Code.Should().Be(2);
    }

    private (int Code, string Output) Git(string args, bool allowFailure = false)
        => GitArgs(args.Split(' '), allowFailure);

    private (int Code, string Output) GitArgs(string[] args, bool allowFailure = false)
    {
        (int code, string output) = Run("git", args, _repo);
        if (!allowFailure && code != 0)
        {
            Assert.Fail($"git {string.Join(' ', args)} failed ({code}): {output}");
        }

        return (code, output);
    }

    private static (int Code, string Output) Run(string exe, string args) => Run(exe, args.Split(' '), null);

    private static (int Code, string Output) Run(string exe, string[] args, string? cwd)
    {
        ProcessStartInfo psi = new(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["DISPLAY"] = string.Empty;
        psi.Environment["DIFFWEAVE_CONFIG_DIR"] = Path.Combine(Path.GetTempPath(), "diffweave-git-test-config");
        using Process p = Process.Start(psi)!;
        Task<string> so = p.StandardOutput.ReadToEndAsync();
        Task<string> se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(true);
            Assert.Fail($"{exe} {string.Join(' ', args)} timed out");
        }

        return (p.ExitCode, so.Result + se.Result);
    }
}
