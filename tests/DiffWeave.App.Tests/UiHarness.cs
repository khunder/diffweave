using Avalonia.Controls;
using DiffWeave.App.Services;
using DiffWeave.App.ViewModels;
using DiffWeave.App.Views;

namespace DiffWeave.App.Tests;

/// <summary>Creates main windows for UI tests with isolated settings, plugins and dialogs.</summary>
public sealed class UiHarness : IDisposable
{
    public UiHarness(PluginService? plugins = null, ThemeMode theme = ThemeMode.Light, Action<AppSettings>? configure = null)
    {
        Directory = TestUtil.CreateTempDirectory();
        SettingsStore store = new(Path.Combine(Directory, "config", "settings.json"));
        AppSettings s = store.Load();
        s.Theme = theme;
        configure?.Invoke(s);
        store.Save(s);
        Plugins = plugins ?? new PluginService([Path.Combine(Directory, "no-plugins")]);
        ViewModel = new MainWindowViewModel(store, Plugins);
        Window = new MainWindow(ViewModel) { Width = 1280, Height = 800 };
        Window.Dialogs = Dialogs;
        Window.Show();
        Window.Pump();
    }

    public string Directory { get; }

    public PluginService Plugins { get; }

    public MainWindowViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public FakeDialogs Dialogs { get; } = new();

    public void Dispose()
    {
        Window.ForceClose();
        if (Avalonia.Application.Current is App app)
        {
            app.ApplyAppearance(ThemeMode.Light, PaletteMode.Standard);
        }

        Plugins.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }
}
