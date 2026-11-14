using Avalonia;
using DiffWeave.App.Cli;

namespace DiffWeave.App;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>Runs the application and returns the process exit code.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        ParseResult parsed = CommandLineParser.Parse(args);
        if (!parsed.Success)
        {
            Console.Error.WriteLine($"diffweave: {parsed.Error}");
            Console.Error.WriteLine("Try 'diffweave --help' for more information.");
            return ExitCodes.UsageError;
        }

        CommandLineOptions options = parsed.Options!;
        if (options.ShowHelp)
        {
            Console.Out.WriteLine(CommandLineParser.Usage);
            return ExitCodes.Success;
        }

        if (options.ShowVersion)
        {
            Console.Out.WriteLine(CommandLineParser.VersionText);
            return ExitCodes.Success;
        }

        if (!options.Quiet)
        {
            foreach (string w in options.Warnings)
            {
                Console.Error.WriteLine($"diffweave: warning: {w}");
            }
        }

        int? headless = NonInteractive.TryRun(options, Console.Error);
        if (headless is { } code)
        {
            return code;
        }

        App.StartupOptions = options;
        App.ExitCode = ExitCodes.Success;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return App.ExitCode;
    }

    /// <summary>Avalonia configuration (also used by the designer and tests).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
