using Avalonia.Controls;
using DiffWeave.App.Cli;

namespace DiffWeave.App.Views;

/// <summary>The main window.</summary>
public class MainWindow : Window
{
    /// <summary>Creates the window for the command line.</summary>
    public static MainWindow CreateForStartup(CommandLineOptions options) => new();
}
