using Avalonia.Threading;
using DiffWeave.Core.Plugins;
using DiffWeave.Extensibility;

namespace DiffWeave.App.Services;

/// <summary>
/// Hosts plugins for the application: loads them with the core <see cref="PluginManager"/> from
/// <c>&lt;appdir&gt;/plugins</c> and <c>$XDG_CONFIG_HOME/diffweave/plugins</c>, and forwards registry changes to the UI thread.
/// </summary>
public sealed class PluginService : IDisposable
{
    private readonly List<(PluginManifest Manifest, IDiffWeavePlugin Plugin)> _builtIns = [];
    private PluginManager _manager;

    /// <summary>Creates the service (does not load plugins yet).</summary>
    public PluginService(IReadOnlyList<string>? directories = null)
    {
        Directories = directories ?? DefaultDirectories();
        _manager = CreateManager();
    }

    /// <summary>Raised on the UI thread when plugins, commands, assistants or decoration providers changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The plugin folders searched, in priority order.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>The core plugin manager.</summary>
    public PluginManager Manager => _manager;

    /// <summary>The default plugin folders: the user folder first, then the application folder.</summary>
    public static IReadOnlyList<string> DefaultDirectories() => [AppPaths.UserPluginDirectory, AppPaths.AppPluginDirectory];

    /// <summary>Registers an in-process plugin (tests, built-in features); it is (re)initialized on every load.</summary>
    public void AddBuiltIn(PluginManifest manifest, IDiffWeavePlugin plugin)
    {
        _builtIns.Add((manifest, plugin));
        _manager.AddBuiltIn(manifest, plugin);
        RaiseChanged();
    }

    /// <summary>Loads all plugins found in <see cref="Directories"/>. Failures are recorded per plugin, never thrown.</summary>
    public IReadOnlyList<PluginInfo> LoadAll()
    {
        IReadOnlyList<PluginInfo> result = _manager.LoadAll();
        RaiseChanged();
        return result;
    }

    /// <summary>Unloads everything and loads again (picks up new or updated plugins).</summary>
    public IReadOnlyList<PluginInfo> Reload()
    {
        Unsubscribe(_manager);
        _manager.Dispose();
        _manager = CreateManager();
        foreach ((PluginManifest m, IDiffWeavePlugin p) in _builtIns)
        {
            _manager.AddBuiltIn(m, p);
        }

        return LoadAll();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Unsubscribe(_manager);
        _manager.Dispose();
    }

    private PluginManager CreateManager()
    {
        JsonSettingsStore? settings = null;
        try
        {
            settings = JsonSettingsStore.Load(Path.Combine(AppPaths.ConfigDirectory, "plugin-settings.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            settings = null;
        }

        PluginManager m = new(new PluginManagerOptions { PluginDirectories = Directories, Settings = settings });
        m.PluginsChanged += OnAnyChanged;
        m.Commands.Changed += OnAnyChanged;
        m.MergeAssistants.Changed += OnAnyChanged;
        m.EditorExtensions.Changed += OnAnyChanged;
        return m;
    }

    private void Unsubscribe(PluginManager m)
    {
        m.PluginsChanged -= OnAnyChanged;
        m.Commands.Changed -= OnAnyChanged;
        m.MergeAssistants.Changed -= OnAnyChanged;
        m.EditorExtensions.Changed -= OnAnyChanged;
    }

    private bool _pending;

    private void OnAnyChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseChanged();
        }
        else if (!_pending)
        {
            _pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _pending = false;
                RaiseChanged();
            });
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
