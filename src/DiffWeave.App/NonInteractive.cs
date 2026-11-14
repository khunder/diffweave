using DiffWeave.App.Cli;

namespace DiffWeave.App;

/// <summary>The non-interactive (--auto) merge path.</summary>
public static class NonInteractive
{
    /// <summary>Runs the merge without UI when possible; returns the exit code, or null when the UI must open.</summary>
    public static int? TryRun(CommandLineOptions options, TextWriter error) => null;
}
