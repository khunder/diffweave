using System.Text;

namespace DiffWeave.App.Cli;

/// <summary>Process exit codes (compatible with <c>git mergetool</c>'s <c>trustExitCode</c>).</summary>
public static class ExitCodes
{
    /// <summary>Merge resolved and saved, or diff closed normally.</summary>
    public const int Success = 0;

    /// <summary>Merge cancelled, left unresolved or not saved.</summary>
    public const int Cancelled = 1;

    /// <summary>Invalid command line or unreadable input.</summary>
    public const int UsageError = 2;
}

/// <summary>What the application should open.</summary>
public enum LaunchMode
{
    /// <summary>Nothing given: open an empty window.</summary>
    Empty,

    /// <summary>Compare two or three files.</summary>
    FileDiff,

    /// <summary>Merge files into an output file.</summary>
    FileMerge,

    /// <summary>Compare two or three directories.</summary>
    DirectoryDiff,
}

/// <summary>Parsed command line.</summary>
public sealed record CommandLineOptions
{
    /// <summary>Positional input paths in pane order (base first for three inputs).</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>The base file given with -b/--base (moved to the front of <see cref="Inputs"/> after parsing).</summary>
    public string? Base { get; init; }

    /// <summary>Output file for merges.</summary>
    public string? Output { get; init; }

    /// <summary>True when -m/--merge was given (or implied by -o).</summary>
    public bool Merge { get; init; }

    /// <summary>Pane labels by input index (L1, L2, L3).</summary>
    public IReadOnlyList<string?> Labels { get; init; } = [null, null, null];

    /// <summary>Auto-merge without UI when there are no unresolved conflicts.</summary>
    public bool Auto { get; init; }

    /// <summary>Quiet: never open the UI from <c>--auto</c>; print nothing but errors.</summary>
    public bool Quiet { get; init; }

    /// <summary>Show help and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Show version and exit.</summary>
    public bool ShowVersion { get; init; }

    /// <summary>
    /// Test hook (undocumented in --help): resolve every remaining conflict with the given side (A, B, C, AB, BA, ...) in
    /// the non-interactive path. Used by integration tests to simulate a user resolving and saving.
    /// </summary>
    public string? TestResolveAll { get; init; }

    /// <summary>Warnings (ignored options) to print.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The resolved launch mode.</summary>
    public LaunchMode Mode { get; init; }
}

/// <summary>Result of <see cref="CommandLineParser.Parse"/>.</summary>
public sealed record ParseResult(CommandLineOptions? Options, string? Error)
{
    /// <summary>True when parsing succeeded.</summary>
    public bool Success => Options is not null;
}

/// <summary>KDiff3-compatible command-line parser.</summary>
public static class CommandLineParser
{
    /// <summary>The help text.</summary>
    public const string Usage = """
        Usage:
          diffweave [OPTIONS] [FILE_A [FILE_B [FILE_C]]]
          diffweave [OPTIONS] DIR_A DIR_B [DIR_C]

        Compares two or three files or directories; with -m/-o merges files into an output file.
        With three inputs the first one is the common base (A), the others are B (local) and C (remote).

        Options:
          -m, --merge            Merge the inputs (without -o the output file is chosen when saving).
          -o, --output FILE      Write the merge result to FILE (implies --merge). Also: --out.
          -b, --base FILE        Use FILE as the common base (A); the positional files become B and C.
              --L1 LABEL         Label for input A (also --L2, --L3 for B and C).
              --fname LABEL      Label for the next input (may be repeated).
          -L LABEL               Same as --fname (diff3 style).
              --auto             Merge without UI when no conflicts remain unresolved; write the output and exit 0.
                                 Otherwise the window opens so that the conflicts can be resolved.
              --qall             Quiet: with --auto never open the window (exit 1 if conflicts remain).
          -h, --help             Show this help.
              --version          Show the version.

        Exit codes: 0 = merge resolved and saved (or comparison closed), 1 = cancelled / unresolved / not saved,
                    2 = invalid command line.

        git integration: see docs/git-integration.md (merge.tool=diffweave, mergetool.diffweave.trustExitCode=true).
        """;

    /// <summary>Parses arguments. File system checks decide between files and directories.</summary>
    public static ParseResult Parse(IReadOnlyList<string> args, Func<string, bool>? isDirectory = null)
    {
        isDirectory ??= Directory.Exists;
        List<string> inputs = [];
        string?[] labels = new string?[3];
        List<string> sequentialLabels = [];
        List<string> warnings = [];
        string? output = null, baseFile = null, testResolve = null;
        bool merge = false, auto = false, quiet = false, help = false, version = false, optionsEnded = false;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (optionsEnded || a.Length < 2 || a[0] != '-')
            {
                inputs.Add(a);
                continue;
            }

            string name = a;
            string? inlineValue = null;
            int eq = a.IndexOf('=', StringComparison.Ordinal);
            if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                name = a[..eq];
                inlineValue = a[(eq + 1)..];
            }

            string? NextValue()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }

                if (i + 1 < args.Count)
                {
                    return args[++i];
                }

                return null;
            }

            switch (name)
            {
                case "--":
                    optionsEnded = true;
                    break;
                case "-h" or "--help" or "-?":
                    help = true;
                    break;
                case "--version" or "-v":
                    version = true;
                    break;
                case "-m" or "--merge":
                    merge = true;
                    break;
                case "--auto":
                    auto = true;
                    break;
                case "--qall" or "-q" or "--quiet":
                    quiet = true;
                    break;
                case "-o" or "--output" or "--out":
                    output = NextValue();
                    if (output is null)
                    {
                        return Fail($"Option {name} requires a file name.");
                    }

                    merge = true;
                    break;
                case "-b" or "--base":
                    baseFile = NextValue();
                    if (baseFile is null)
                    {
                        return Fail($"Option {name} requires a file name.");
                    }

                    break;
                case "--L1" or "--L2" or "--L3":
                    string? label = NextValue();
                    if (label is null)
                    {
                        return Fail($"Option {name} requires a label.");
                    }

                    labels[name[3] - '1'] = label;
                    break;
                case "--fname" or "-L":
                    string? f = NextValue();
                    if (f is null)
                    {
                        return Fail($"Option {name} requires a label.");
                    }

                    sequentialLabels.Add(f);
                    break;
                case "--test-resolve-all":
                    testResolve = NextValue();
                    if (testResolve is null || !IsValidChoice(testResolve))
                    {
                        return Fail("--test-resolve-all requires A, B, C, AB, BA, AC, CA, BC or CB.");
                    }

                    break;
                case "--cs" or "--confighelp" or "-u":
                    if (name == "--cs")
                    {
                        NextValue();
                    }

                    warnings.Add($"Option {name} is not supported and was ignored.");
                    break;
                default:
                    if (a.StartsWith("-o", StringComparison.Ordinal) && a.Length > 2)
                    {
                        output = a[2..];
                        merge = true;
                        break;
                    }

                    return Fail($"Unknown option '{a}'.");
            }
        }

        if (baseFile is not null)
        {
            inputs.Insert(0, baseFile);
        }

        if (inputs.Count > 3)
        {
            return Fail("At most three inputs can be compared.");
        }

        for (int i = 0, j = 0; i < sequentialLabels.Count && j < 3; j++)
        {
            if (labels[j] is null)
            {
                labels[j] = sequentialLabels[i++];
            }
        }

        LaunchMode mode;
        bool anyDir = inputs.Any(isDirectory);
        if (inputs.Count == 0)
        {
            if (merge && output is null)
            {
                return Fail("--merge needs input files.");
            }

            mode = LaunchMode.Empty;
        }
        else if (anyDir)
        {
            if (!inputs.All(isDirectory))
            {
                return Fail("Cannot compare a directory with a file.");
            }

            if (inputs.Count < 2)
            {
                return Fail("Directory comparison needs two or three directories.");
            }

            if (merge)
            {
                warnings.Add("Directory merge is not supported; opening a directory comparison.");
                merge = false;
                output = null;
            }

            mode = LaunchMode.DirectoryDiff;
        }
        else if (merge)
        {
            if (inputs.Count < 2)
            {
                return Fail("Merging needs at least two input files.");
            }

            if (auto && output is null)
            {
                return Fail("--auto needs an output file (-o FILE).");
            }

            mode = LaunchMode.FileMerge;
        }
        else
        {
            mode = LaunchMode.FileDiff;
        }

        if (auto && mode != LaunchMode.FileMerge && !help && !version)
        {
            warnings.Add("--auto only applies to merges and was ignored.");
            auto = false;
        }

        return new ParseResult(
            new CommandLineOptions
            {
                Inputs = inputs,
                Base = baseFile,
                Output = output,
                Merge = merge,
                Labels = labels,
                Auto = auto,
                Quiet = quiet,
                ShowHelp = help,
                ShowVersion = version,
                TestResolveAll = testResolve?.ToUpperInvariant(),
                Warnings = warnings,
                Mode = mode,
            },
            null);
    }

    private static bool IsValidChoice(string s) => s.ToUpperInvariant() is "A" or "B" or "C" or "AB" or "BA" or "AC" or "CA" or "BC" or "CB";

    private static ParseResult Fail(string error) => new(null, error);

    /// <summary>The version string printed by --version.</summary>
    public static string VersionText
    {
        get
        {
            string v = typeof(CommandLineParser).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            StringBuilder sb = new();
            sb.Append("diffweave ").Append(v);
            return sb.ToString();
        }
    }
}
