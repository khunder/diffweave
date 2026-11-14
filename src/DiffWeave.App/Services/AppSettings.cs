using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffWeave.App.Services;

/// <summary>Light/dark selection.</summary>
public enum ThemeMode
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Light theme.</summary>
    Light,

    /// <summary>Dark theme.</summary>
    Dark,
}

/// <summary>Diff colour palette.</summary>
public enum PaletteMode
{
    /// <summary>Red/green/blue diff colours.</summary>
    Standard,

    /// <summary>Okabe-Ito based colour-blind safe palette (blue/orange/yellow).</summary>
    ColorBlind,
}

/// <summary>How whitespace differences are treated.</summary>
public enum WhitespaceSetting
{
    /// <summary>Whitespace is significant.</summary>
    Compare,

    /// <summary>Ignore whitespace at line ends.</summary>
    IgnoreTrailing,

    /// <summary>Ignore changes in the amount of whitespace.</summary>
    IgnoreChanges,

    /// <summary>Ignore all whitespace.</summary>
    IgnoreAll,
}

/// <summary>Persisted user settings (JSON in the XDG config directory).</summary>
public sealed class AppSettings
{
    /// <summary>Light/dark theme.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Diff colour palette.</summary>
    public PaletteMode Palette { get; set; } = PaletteMode.Standard;

    /// <summary>Editor font family list.</summary>
    public string FontFamily { get; set; } = Controls.DiffTextView.DefaultFontFamily;

    /// <summary>Editor font size.</summary>
    public double FontSize { get; set; } = 13;

    /// <summary>Tab width.</summary>
    public int TabSize { get; set; } = 4;

    /// <summary>Word wrap in the output pane.</summary>
    public bool WordWrap { get; set; }

    /// <summary>Show whitespace glyphs.</summary>
    public bool ShowWhitespace { get; set; }

    /// <summary>Show line numbers.</summary>
    public bool ShowLineNumbers { get; set; } = true;

    /// <summary>Whitespace handling for diffs.</summary>
    public WhitespaceSetting Whitespace { get; set; } = WhitespaceSetting.Compare;

    /// <summary>Ignore case differences.</summary>
    public bool IgnoreCase { get; set; }

    /// <summary>Ignore blank lines.</summary>
    public bool IgnoreBlankLines { get; set; }

    /// <summary>Show the assistants side panel.</summary>
    public bool ShowAssistantsPanel { get; set; }

    /// <summary>Last used layout ("TwoWay", "ThreeWay", "Merge", "Directory").</summary>
    public string LastLayout { get; set; } = "TwoWay";

    /// <summary>Recently opened comparisons (most recent first). Each entry is a list of paths.</summary>
    public List<List<string>> RecentFiles { get; set; } = [];

    /// <summary>Main window width.</summary>
    public double WindowWidth { get; set; } = 1280;

    /// <summary>Main window height.</summary>
    public double WindowHeight { get; set; } = 820;

    /// <summary>Maximum number of recent entries kept.</summary>
    public const int MaxRecent = 12;

    /// <summary>Adds a recent entry, moving duplicates to the top.</summary>
    public void AddRecent(IReadOnlyList<string> paths)
    {
        List<string> entry = [.. paths.Select(Path.GetFullPath)];
        RecentFiles.RemoveAll(r => r.SequenceEqual(entry, StringComparer.Ordinal));
        RecentFiles.Insert(0, entry);
        if (RecentFiles.Count > MaxRecent)
        {
            RecentFiles.RemoveRange(MaxRecent, RecentFiles.Count - MaxRecent);
        }
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Creates a store for a settings file.</summary>
    public SettingsStore(string filePath) => FilePath = filePath;

    /// <summary>The settings file.</summary>
    public string FilePath { get; }

    /// <summary>The store for the default location.</summary>
    public static SettingsStore Default => new(Path.Combine(AppPaths.ConfigDirectory, "settings.json"));

    /// <summary>Loads settings; returns defaults when the file is missing or invalid (first run).</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (s is not null)
                {
                    s.TabSize = Math.Clamp(s.TabSize, 1, 16);
                    s.FontSize = Math.Clamp(s.FontSize, 6, 48);
                    s.RecentFiles ??= [];
                    return s;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start with defaults.
        }

        return new AppSettings();
    }

    /// <summary>Saves settings atomically. Errors are swallowed (settings are best-effort).</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>XDG-aware application directories.</summary>
public static class AppPaths
{
    /// <summary>Overrides the configuration directory (tests).</summary>
    public static string? ConfigDirectoryOverride { get; set; }

    /// <summary>$XDG_CONFIG_HOME/diffweave (or ~/.config/diffweave; %APPDATA%\diffweave on Windows).</summary>
    public static string ConfigDirectory
    {
        get
        {
            if (ConfigDirectoryOverride is { } o)
            {
                return o;
            }

            string? env = Environment.GetEnvironmentVariable("DIFFWEAVE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "diffweave");
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "diffweave");
        }
    }

    /// <summary>The user plugin directory.</summary>
    public static string UserPluginDirectory => Path.Combine(ConfigDirectory, "plugins");

    /// <summary>The plugin directory next to the executable.</summary>
    public static string AppPluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");
}
