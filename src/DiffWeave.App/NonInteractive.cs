using DiffWeave.App.Cli;
using DiffWeave.App.Documents;
using DiffWeave.App.Services;
using DiffWeave.Core.Diff;
using DiffWeave.Core.Merge;

namespace DiffWeave.App;

/// <summary>
/// The non-interactive merge path used by <c>--auto</c> (and the <c>--test-resolve-all</c> test hook): merges without
/// starting the UI when nothing remains unresolved, writes the output and returns exit code 0.
/// </summary>
public static class NonInteractive
{
    /// <summary>Runs the merge without UI when possible; returns the exit code, or null when the UI must open.</summary>
    public static int? TryRun(CommandLineOptions options, TextWriter error, AppSettings? settings = null)
    {
        if (options.Mode != LaunchMode.FileMerge || (!options.Auto && options.TestResolveAll is null))
        {
            return null;
        }

        if (options.Output is null)
        {
            error.WriteLine("diffweave: --auto needs an output file (-o FILE).");
            return ExitCodes.UsageError;
        }

        settings ??= SettingsStore.Default.Load();
        MergeDocument doc;
        try
        {
            List<InputFile> inputs = [];
            for (int i = 0; i < options.Inputs.Count; i++)
            {
                string path = options.Inputs[i];
                string? label = i < options.Labels.Count ? options.Labels[i] : null;
                inputs.Add(InputFile.IsNullDevice(path) ? InputFile.FromText(label ?? "(no file)", string.Empty)
                    : File.Exists(path) ? InputFile.Load(path, label)
                    : InputFile.FromText(label ?? path, string.Empty, path));
            }

            InputFile? baseFile = inputs.Count == 3 ? inputs[0] : null;
            doc = MergeDocument.Create(baseFile, inputs[^2], inputs[^1], options.Output, OptionsFrom(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"diffweave: {ex.Message}");
            return ExitCodes.UsageError;
        }

        if (options.TestResolveAll is { } side)
        {
            doc.ResolveAll(side switch
            {
                "A" => ResolutionChoice.Base,
                "B" => ResolutionChoice.Left,
                "C" => ResolutionChoice.Right,
                "BC" or "AB" => ResolutionChoice.LeftThenRight,
                _ => ResolutionChoice.RightThenLeft,
            });
        }

        if (doc.UnresolvedCount == 0)
        {
            try
            {
                doc.SaveCore(options.Output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"diffweave: cannot write {options.Output}: {ex.Message}");
                return ExitCodes.Cancelled;
            }

            if (!options.Quiet)
            {
                error.WriteLine($"diffweave: merged {Path.GetFileName(options.Output)} automatically ({doc.ConflictCount} conflict(s) resolved).");
            }

            return ExitCodes.Success;
        }

        if (options.Quiet)
        {
            error.WriteLine($"diffweave: {doc.UnresolvedCount} unresolved conflict(s) in {options.Output}; not saved.");
            return ExitCodes.Cancelled;
        }

        return null;
    }

    /// <summary>Diff options from the persisted settings.</summary>
    public static DiffOptions OptionsFrom(AppSettings s) => new()
    {
        Algorithm = DiffAlgorithm.Histogram,
        Whitespace = s.Whitespace switch
        {
            WhitespaceSetting.IgnoreTrailing => WhitespaceMode.IgnoreTrailing,
            WhitespaceSetting.IgnoreChanges => WhitespaceMode.IgnoreAmount,
            WhitespaceSetting.IgnoreAll => WhitespaceMode.IgnoreAll,
            _ => WhitespaceMode.None,
        },
        IgnoreCase = s.IgnoreCase,
        IgnoreBlankLines = s.IgnoreBlankLines,
    };
}
