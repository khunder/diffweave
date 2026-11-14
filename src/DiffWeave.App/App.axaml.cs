using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using DiffWeave.App.Services;

namespace DiffWeave.App;

/// <summary>The Avalonia application.</summary>
public partial class App : Application
{
    private ResourceInclude? _colorBlind;

    /// <summary>Options from the command line; set by <see cref="Program"/> before start-up.</summary>
    public static Cli.CommandLineOptions? StartupOptions { get; set; }

    /// <summary>The process exit code decided by the main window.</summary>
    public static int ExitCode { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Applies theme and palette settings to the application resources.</summary>
    public void ApplyAppearance(ThemeMode theme, PaletteMode palette)
    {
        RequestedThemeVariant = theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var merged = Resources.MergedDictionaries;
        if (palette == PaletteMode.ColorBlind && _colorBlind is null)
        {
            _colorBlind = new ResourceInclude(new Uri("avares://diffweave/App.axaml"))
            {
                Source = new Uri("avares://diffweave/Themes/Palette.ColorBlind.axaml"),
            };
            merged.Add(_colorBlind);
        }
        else if (palette != PaletteMode.ColorBlind && _colorBlind is not null)
        {
            merged.Remove(_colorBlind);
            _colorBlind = null;
        }
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = Views.MainWindow.CreateForStartup(StartupOptions ?? new Cli.CommandLineOptions());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
