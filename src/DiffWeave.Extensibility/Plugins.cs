namespace DiffWeave.Extensibility;

/// <summary>
/// Entry point of a plugin. The host instantiates the type named by <see cref="PluginManifest.EntryType"/>
/// (it needs a public parameterless constructor), then calls <see cref="Initialize"/> once.
/// </summary>
/// <remarks>
/// Everything a plugin contributes goes through the <see cref="IPluginHost"/> registries. Registrations made
/// through the host are removed automatically when the plugin is unloaded, so <see cref="Shutdown"/> only needs to
/// release resources the plugin created itself. Exceptions thrown from either method are caught and logged by the
/// host; a failing plugin is marked as faulted and never takes the application down.
/// </remarks>
public interface IDiffWeavePlugin
{
    /// <summary>Called once after loading. Register commands, assistants and editor extensions here.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Called once before the plugin is unloaded (or the application exits).</summary>
    void Shutdown();
}

/// <summary>
/// The contents of a plugin's <c>plugin.json</c> manifest.
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// {
///   "id": "diffweave.samples.whitespace",
///   "name": "Whitespace normalizer",
///   "version": "1.0.0",
///   "apiVersion": "[1.0,2.0)",
///   "entryAssembly": "DiffWeave.Samples.WhitespacePlugin.dll",
///   "entryType": "DiffWeave.Samples.WhitespacePlugin.WhitespacePlugin",
///   "description": "Proposes resolutions for conflicts that differ only in whitespace.",
///   "authors": [ "DiffWeave contributors" ]
/// }
/// </code>
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>Unique, stable identifier (reverse-DNS style recommended). Letters, digits, '.', '-' and '_' only.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>The plugin's own version (any <see cref="System.Version"/>-parsable string, e.g. "1.2.0").</summary>
    public required string Version { get; init; }

    /// <summary>The range of extension API versions the plugin supports, see <see cref="ApiVersionRange"/>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>File name of the entry assembly, relative to the plugin folder.</summary>
    public required string EntryAssembly { get; init; }

    /// <summary>Full name of the type implementing <see cref="IDiffWeavePlugin"/>.</summary>
    public required string EntryType { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional list of authors.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];
}

/// <summary>The lifecycle state of a plugin known to the host.</summary>
public enum PluginState
{
    /// <summary>Found and its manifest is valid, but it is not loaded.</summary>
    Discovered,

    /// <summary>Loaded and initialized successfully.</summary>
    Loaded,

    /// <summary>The manifest could not be read or is invalid.</summary>
    InvalidManifest,

    /// <summary>The plugin requires an API version range that does not include the host's version.</summary>
    Incompatible,

    /// <summary>Loading or initialization threw; see <see cref="PluginInfo.Error"/>.</summary>
    Faulted,

    /// <summary>Disabled by the user or by configuration.</summary>
    Disabled,

    /// <summary>Was loaded and has been unloaded.</summary>
    Unloaded,
}

/// <summary>Read-only description of a plugin as seen by the host and by the plugin itself.</summary>
/// <param name="Manifest">The manifest; <see langword="null"/> when <see cref="State"/> is <see cref="PluginState.InvalidManifest"/>.</param>
/// <param name="Directory">The folder the plugin was found in.</param>
/// <param name="State">The current lifecycle state.</param>
/// <param name="Error">A description of what went wrong for invalid, incompatible or faulted plugins.</param>
public sealed record PluginInfo(PluginManifest? Manifest, string Directory, PluginState State, string? Error = null)
{
    /// <summary>The plugin id, or the folder name when the manifest is missing.</summary>
    public string Id => Manifest?.Id ?? System.IO.Path.GetFileName(Directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}

/// <summary>
/// The services the host offers to a plugin. Each plugin receives its own instance; registrations made through it
/// are tracked and removed when the plugin is unloaded.
/// </summary>
public interface IPluginHost
{
    /// <summary>Information about the plugin this host instance belongs to.</summary>
    PluginInfo Plugin { get; }

    /// <summary>The API version implemented by the host (equal to <see cref="ExtensibilityApi.Version"/> of the host's copy).</summary>
    Version HostApiVersion { get; }

    /// <summary>Additional host services (for example <see cref="IFileTypeService"/>). Returns <see langword="null"/> for unknown services.</summary>
    IServiceProvider Services { get; }

    /// <summary>Command contributions (menu items and shortcuts).</summary>
    ICommandRegistry Commands { get; }

    /// <summary>Merge assistants.</summary>
    IMergeAssistantRegistry MergeAssistants { get; }

    /// <summary>Editor decorations.</summary>
    IEditorExtensionRegistry EditorExtensions { get; }

    /// <summary>Merge lifecycle hooks.</summary>
    IMergeLifecycleHookRegistry LifecycleHooks { get; }

    /// <summary>Language detection.</summary>
    IFileTypeService FileTypes { get; }

    /// <summary>Settings private to this plugin (namespace = plugin id).</summary>
    ISettingsStore Settings { get; }

    /// <summary>A logger whose entries are tagged with the plugin id.</summary>
    IPluginLogger Logger { get; }
}

/// <summary>Severity of a log entry.</summary>
public enum PluginLogLevel
{
    /// <summary>Verbose diagnostic information.</summary>
    Debug,

    /// <summary>Normal operation.</summary>
    Information,

    /// <summary>Something unexpected that the plugin recovered from.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>Logging for plugins and the plugin infrastructure.</summary>
public interface IPluginLogger
{
    /// <summary>Writes an entry.</summary>
    void Log(PluginLogLevel level, string message, Exception? exception = null);
}

/// <summary>Convenience methods for <see cref="IPluginLogger"/>.</summary>
public static class PluginLoggerExtensions
{
    /// <summary>Logs at <see cref="PluginLogLevel.Debug"/>.</summary>
    public static void Debug(this IPluginLogger logger, string message) => logger.Log(PluginLogLevel.Debug, message);

    /// <summary>Logs at <see cref="PluginLogLevel.Information"/>.</summary>
    public static void Info(this IPluginLogger logger, string message) => logger.Log(PluginLogLevel.Information, message);

    /// <summary>Logs at <see cref="PluginLogLevel.Warning"/>.</summary>
    public static void Warn(this IPluginLogger logger, string message, Exception? exception = null) => logger.Log(PluginLogLevel.Warning, message, exception);

    /// <summary>Logs at <see cref="PluginLogLevel.Error"/>.</summary>
    public static void Error(this IPluginLogger logger, string message, Exception? exception = null) => logger.Log(PluginLogLevel.Error, message, exception);
}
