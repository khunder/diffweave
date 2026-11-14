using System.Reflection;
using System.Runtime.CompilerServices;
using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>Options of <see cref="PluginManager"/>.</summary>
public sealed record PluginManagerOptions
{
    /// <summary>
    /// Folders to scan. Every immediate sub-folder containing a <c>plugin.json</c> is a plugin (a scanned folder that
    /// itself contains <c>plugin.json</c> is a plugin as well). Missing folders are ignored.
    /// </summary>
    public IReadOnlyList<string> PluginDirectories { get; init; } = [];

    /// <summary>The API version the host implements (override only for tests).</summary>
    public Version HostApiVersion { get; init; } = ExtensibilityApi.Version;

    /// <summary>Root settings store; each plugin gets the section "plugins/&lt;id&gt;". In-memory when <see langword="null"/>.</summary>
    public ISettingsStore? Settings { get; init; }

    /// <summary>Log buffer shared by host and plugins. A new buffer when <see langword="null"/>.</summary>
    public PluginLogBuffer? Log { get; init; }

    /// <summary>Plugin ids that are discovered but not loaded.</summary>
    public IReadOnlyCollection<string> DisabledPluginIds { get; init; } = [];

    /// <summary>Additional services exposed through <see cref="IPluginHost.Services"/>.</summary>
    public IServiceProvider? Services { get; init; }
}

/// <summary>
/// Discovers, loads and unloads plugins and owns the registries they contribute to. Each plugin is loaded into its
/// own collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>; failures while loading, initializing or
/// shutting down a plugin are caught, logged and reflected in <see cref="PluginInfo.State"/>.
/// </summary>
/// <remarks>Public members are thread-safe; loading and unloading are serialized.</remarks>
public sealed class PluginManager : IDisposable
{
    private const string HostSource = "host";

    private readonly Lock _gate = new();
    private readonly PluginManagerOptions _options;
    private readonly IPluginLogger _hostLogger;
    private readonly Dictionary<string, PluginEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<PluginInfo> _invalid = [];

    /// <summary>Creates a manager.</summary>
    public PluginManager(PluginManagerOptions? options = null)
    {
        _options = options ?? new PluginManagerOptions();
        Log = _options.Log ?? new PluginLogBuffer();
        _hostLogger = Log.CreateLogger(HostSource);
        Settings = _options.Settings ?? new JsonSettingsStore();
        Commands = new CommandRegistry(_hostLogger);
        MergeAssistants = new MergeAssistantRegistry(_hostLogger);
        EditorExtensions = new EditorExtensionRegistry(_hostLogger);
        LifecycleHooks = new MergeLifecycleHookRegistry(_hostLogger);
        FileTypes = new FileTypeService();
    }

    /// <summary>The command registry.</summary>
    public CommandRegistry Commands { get; }

    /// <summary>The merge assistant registry.</summary>
    public MergeAssistantRegistry MergeAssistants { get; }

    /// <summary>The editor extension registry.</summary>
    public EditorExtensionRegistry EditorExtensions { get; }

    /// <summary>The lifecycle hook registry.</summary>
    public MergeLifecycleHookRegistry LifecycleHooks { get; }

    /// <summary>The file type service.</summary>
    public FileTypeService FileTypes { get; }

    /// <summary>The root settings store.</summary>
    public ISettingsStore Settings { get; }

    /// <summary>The log of host and plugins.</summary>
    public PluginLogBuffer Log { get; }

    /// <summary>The host's API version.</summary>
    public Version HostApiVersion => _options.HostApiVersion;

    /// <summary>All known plugins (discovered, loaded, failed...), ordered by id.</summary>
    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values.Select(e => e.Info).Concat(_invalid).OrderBy(i => i.Id, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Raised after the plugin list or a plugin state changed.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>
    /// Scans <see cref="PluginManagerOptions.PluginDirectories"/> and records every plugin found (without loading).
    /// Already known plugins keep their state. Returns <see cref="Plugins"/>.
    /// </summary>
    public IReadOnlyList<PluginInfo> Discover()
    {
        lock (_gate)
        {
            _invalid.Clear();
            foreach (string root in _options.PluginDirectories)
            {
                foreach (string dir in CandidateFolders(root))
                {
                    DiscoverFolder(dir);
                }
            }
        }

        RaisePluginsChanged();
        return Plugins;
    }

    /// <summary>Discovers and loads all compatible, enabled plugins. Returns <see cref="Plugins"/>.</summary>
    public IReadOnlyList<PluginInfo> LoadAll()
    {
        Discover();
        string[] ids;
        lock (_gate)
        {
            ids = [.. _entries.Values.Where(e => e.Info.State is PluginState.Discovered or PluginState.Unloaded).Select(e => e.Info.Id)];
        }

        foreach (string id in ids)
        {
            Load(id);
        }

        return Plugins;
    }

    /// <summary>Loads a discovered plugin by id. Returns its new info (never throws for plugin failures).</summary>
    public PluginInfo Load(string pluginId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out PluginEntry? entry))
            {
                throw new ArgumentException($"Unknown plugin '{pluginId}'.", nameof(pluginId));
            }

            if (entry.Info.State == PluginState.Loaded || entry.Info.State == PluginState.Incompatible)
            {
                return entry.Info;
            }

            if (_options.DisabledPluginIds.Contains(pluginId))
            {
                entry.Info = entry.Info with { State = PluginState.Disabled, Error = null };
            }
            else
            {
                LoadEntry(entry);
            }
        }

        RaisePluginsChanged();
        return GetInfo(pluginId);
    }

    /// <summary>Loads the plugin in <paramref name="pluginDirectory"/> (discovering it first). Returns its info.</summary>
    public PluginInfo LoadFrom(string pluginDirectory)
    {
        PluginInfo info;
        lock (_gate)
        {
            info = DiscoverFolder(Path.GetFullPath(pluginDirectory));
        }

        if (info.State == PluginState.InvalidManifest)
        {
            RaisePluginsChanged();
            return info;
        }

        return Load(info.Id);
    }

    /// <summary>
    /// Registers an in-process plugin (no separate load context, cannot be collected). Useful for built-in
    /// contributions and tests. The plugin is initialized immediately.
    /// </summary>
    public PluginInfo AddBuiltIn(PluginManifest manifest, IDiffWeavePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(plugin);
        lock (_gate)
        {
            if (_entries.ContainsKey(manifest.Id))
            {
                throw new InvalidOperationException($"A plugin with id '{manifest.Id}' is already known.");
            }

            var entry = new PluginEntry(new PluginInfo(manifest, AppContext.BaseDirectory, PluginState.Discovered)) { BuiltIn = plugin };
            _entries.Add(manifest.Id, entry);
            if (!CheckCompatible(entry))
            {
                return entry.Info;
            }

            InitializeEntry(entry, plugin, null);
        }

        RaisePluginsChanged();
        return GetInfo(manifest.Id);
    }

    /// <summary>Unloads a plugin: removes its registrations, calls <see cref="IDiffWeavePlugin.Shutdown"/> and unloads its load context.</summary>
    /// <returns>Whether the plugin was loaded.</returns>
    public bool Unload(string pluginId) => Unload(pluginId, out _);

    /// <summary>
    /// Unloads a plugin and returns a weak reference to its load context, which becomes dead once the runtime has
    /// collected it (useful to verify that a plugin does not leak).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Unload(string pluginId, out WeakReference? loadContext)
    {
        loadContext = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(pluginId, out PluginEntry? entry) || entry.Info.State != PluginState.Loaded)
            {
                return false;
            }

            loadContext = entry.Context is null ? null : new WeakReference(entry.Context);
            Teardown(entry, callShutdown: true);
            entry.Info = entry.Info with { State = PluginState.Unloaded, Error = null };
        }

        RaisePluginsChanged();
        return true;
    }

    /// <summary>Unloads all loaded plugins.</summary>
    public void UnloadAll()
    {
        string[] ids;
        lock (_gate)
        {
            ids = [.. _entries.Values.Where(e => e.Info.State == PluginState.Loaded).Select(e => e.Info.Id)];
        }

        foreach (string id in ids)
        {
            Unload(id);
        }
    }

    /// <summary>Unloads all plugins.</summary>
    public void Dispose() => UnloadAll();

    /// <summary>Creates an <see cref="IMergeAssistant"/> run with failure isolation; see <see cref="MergeAssistantRunner"/>.</summary>
    public Task<MergeAssistantRunResult> RunAssistantAsync(IMergeAssistant assistant, MergeAssistantRequest request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => MergeAssistantRunner.RunAsync(assistant, request, timeout, _hostLogger, cancellationToken);

    private PluginInfo GetInfo(string id)
    {
        lock (_gate)
        {
            return _entries[id].Info;
        }
    }

    private static IEnumerable<string> CandidateFolders(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        if (File.Exists(Path.Combine(root, ExtensibilityApi.ManifestFileName)))
        {
            yield return Path.GetFullPath(root);
            yield break;
        }

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(subdirs, StringComparer.Ordinal);
        foreach (string dir in subdirs)
        {
            if (File.Exists(Path.Combine(dir, ExtensibilityApi.ManifestFileName)))
            {
                yield return Path.GetFullPath(dir);
            }
        }
    }

    // Must hold _gate.
    private PluginInfo DiscoverFolder(string dir)
    {
        PluginManifest? manifest = PluginManifestReader.Read(dir, out IReadOnlyList<string> errors);
        if (manifest is null)
        {
            var bad = new PluginInfo(null, dir, PluginState.InvalidManifest, string.Join(" ", errors));
            _invalid.Add(bad);
            _hostLogger.Warn($"Ignoring plugin folder '{dir}': {bad.Error}");
            return bad;
        }

        if (_entries.TryGetValue(manifest.Id, out PluginEntry? existing))
        {
            if (!PathsEqual(existing.Info.Directory, dir))
            {
                var dup = new PluginInfo(manifest, dir, PluginState.InvalidManifest, $"Duplicate plugin id '{manifest.Id}' (already provided by '{existing.Info.Directory}').");
                _invalid.Add(dup);
                _hostLogger.Warn(dup.Error!);
                return dup;
            }

            if (existing.Info.State is PluginState.Discovered or PluginState.Unloaded or PluginState.Faulted or PluginState.Disabled)
            {
                existing.Info = existing.Info with { Manifest = manifest };
            }

            return existing.Info;
        }

        var entry = new PluginEntry(new PluginInfo(manifest, dir, PluginState.Discovered));
        _entries.Add(manifest.Id, entry);
        CheckCompatible(entry);
        return entry.Info;
    }

    private bool CheckCompatible(PluginEntry entry)
    {
        PluginManifest manifest = entry.Info.Manifest!;
        if (!ApiVersionRange.TryParse(manifest.ApiVersion, out ApiVersionRange? range) || !range.Contains(HostApiVersion))
        {
            entry.Info = entry.Info with
            {
                State = PluginState.Incompatible,
                Error = $"The plugin requires extension API {manifest.ApiVersion}; this host implements {HostApiVersion.ToString(2)}.",
            };
            _hostLogger.Warn($"Plugin '{manifest.Id}' is incompatible: {entry.Info.Error}");
            return false;
        }

        return true;
    }

    // Must hold _gate.
    private void LoadEntry(PluginEntry entry)
    {
        PluginManifest manifest = entry.Info.Manifest!;
        if (!CheckCompatible(entry))
        {
            return;
        }

        if (entry.BuiltIn is not null)
        {
            InitializeEntry(entry, entry.BuiltIn, null);
            return;
        }

        string assemblyPath = Path.GetFullPath(Path.Combine(entry.Info.Directory, manifest.EntryAssembly));
        PluginLoadContext? context = null;
        IDiffWeavePlugin plugin;
        try
        {
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException($"Entry assembly '{manifest.EntryAssembly}' not found.", assemblyPath);
            }

            context = new PluginLoadContext(manifest.Id, assemblyPath);
            Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
            Type type = assembly.GetType(manifest.EntryType, throwOnError: false)
                ?? throw new TypeLoadException($"Entry type '{manifest.EntryType}' not found in '{manifest.EntryAssembly}'.");
            if (!typeof(IDiffWeavePlugin).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"Entry type '{manifest.EntryType}' does not implement {nameof(IDiffWeavePlugin)} (or was built against an incompatible copy of DiffWeave.Extensibility).");
            }

            plugin = (IDiffWeavePlugin)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Could not create '{manifest.EntryType}'."));
        }
        catch (Exception ex)
        {
            Fault(entry, "Loading failed", ex);
            context?.Unload();
            return;
        }

        InitializeEntry(entry, plugin, context);
    }

    // Must hold _gate.
    private void InitializeEntry(PluginEntry entry, IDiffWeavePlugin plugin, PluginLoadContext? context)
    {
        PluginInfo loadedInfo = entry.Info with { State = PluginState.Loaded, Error = null };
        var host = new PluginHost(this, loadedInfo);
        entry.Plugin = plugin;
        entry.Context = context;
        entry.Host = host;
        try
        {
            plugin.Initialize(host);
        }
        catch (Exception ex)
        {
            Teardown(entry, callShutdown: false);
            Fault(entry, "Initialization failed", ex);
            return;
        }

        entry.Info = loadedInfo;
        _hostLogger.Info($"Loaded plugin '{loadedInfo.Id}' {loadedInfo.Manifest?.Version}.");
    }

    private void Fault(PluginEntry entry, string what, Exception ex)
    {
        Exception inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
        entry.Info = entry.Info with { State = PluginState.Faulted, Error = $"{what}: {inner.GetType().Name}: {inner.Message}" };
        _hostLogger.Error($"Plugin '{entry.Info.Id}': {entry.Info.Error}", inner);
    }

    // Must hold _gate.
    private void Teardown(PluginEntry entry, bool callShutdown)
    {
        entry.Host?.DisposeRegistrations();
        if (callShutdown && entry.Plugin is not null)
        {
            try
            {
                entry.Plugin.Shutdown();
            }
            catch (Exception ex)
            {
                _hostLogger.Error($"Plugin '{entry.Info.Id}' threw during shutdown.", ex);
            }
        }

        entry.Plugin = null;
        entry.Host = null;
        PluginLoadContext? context = entry.Context;
        entry.Context = null;
        context?.Unload();
    }

    private void RaisePluginsChanged()
    {
        try
        {
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _hostLogger.Error("A PluginsChanged handler threw.", ex);
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class PluginEntry(PluginInfo info)
    {
        public PluginInfo Info { get; set; } = info;

        public IDiffWeavePlugin? BuiltIn { get; init; }

        public IDiffWeavePlugin? Plugin { get; set; }

        public PluginLoadContext? Context { get; set; }

        public PluginHost? Host { get; set; }
    }

    /// <summary>The per-plugin host; tracks registrations so they can be removed on unload.</summary>
    private sealed class PluginHost : IPluginHost, IServiceProvider
    {
        private readonly PluginManager _manager;
        private readonly List<IDisposable> _registrations = [];
        private readonly Lock _gate = new();

        public PluginHost(PluginManager manager, PluginInfo info)
        {
            _manager = manager;
            Plugin = info;
            Logger = manager.Log.CreateLogger(info.Id);
            Settings = new TrackingSettings(this, manager.Settings.GetSection("plugins").GetSection(info.Id));
            Commands = new TrackingCommands(this, manager.Commands);
            MergeAssistants = new TrackingAssistants(this, manager.MergeAssistants);
            EditorExtensions = new TrackingEditor(this, manager.EditorExtensions);
            LifecycleHooks = new TrackingHooks(this, manager.LifecycleHooks);
            FileTypes = new TrackingFileTypes(this, manager.FileTypes);
        }

        public PluginInfo Plugin { get; }

        public Version HostApiVersion => _manager.HostApiVersion;

        public IServiceProvider Services => this;

        public ICommandRegistry Commands { get; }

        public IMergeAssistantRegistry MergeAssistants { get; }

        public IEditorExtensionRegistry EditorExtensions { get; }

        public IMergeLifecycleHookRegistry LifecycleHooks { get; }

        public IFileTypeService FileTypes { get; }

        public ISettingsStore Settings { get; }

        public IPluginLogger Logger { get; }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IFileTypeService))
            {
                return FileTypes;
            }

            if (serviceType == typeof(ISettingsStore))
            {
                return Settings;
            }

            if (serviceType == typeof(IPluginLogger))
            {
                return Logger;
            }

            return _manager._options.Services?.GetService(serviceType);
        }

        public IDisposable Track(IDisposable registration)
        {
            lock (_gate)
            {
                _registrations.Add(registration);
            }

            return new Registration(() =>
            {
                lock (_gate)
                {
                    _registrations.Remove(registration);
                }

                registration.Dispose();
            });
        }

        public void DisposeRegistrations()
        {
            IDisposable[] regs;
            lock (_gate)
            {
                regs = [.. _registrations];
                _registrations.Clear();
            }

            foreach (IDisposable r in regs.Reverse())
            {
                try
                {
                    r.Dispose();
                }
                catch (Exception)
                {
                    // Removing a registration must not fail.
                }
            }
        }
    }

    private sealed class TrackingCommands(PluginHost host, CommandRegistry inner) : ICommandRegistry
    {
        public IReadOnlyList<CommandContribution> Commands => inner.Commands;

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IDisposable Register(CommandContribution command) => host.Track(inner.Register(command));

        public bool CanExecute(string commandId, CommandContext context) => inner.CanExecute(commandId, context);

        public Task<bool> ExecuteAsync(string commandId, CommandContext context, CancellationToken cancellationToken = default)
            => inner.ExecuteAsync(commandId, context, cancellationToken);
    }

    private sealed class TrackingAssistants(PluginHost host, MergeAssistantRegistry inner) : IMergeAssistantRegistry
    {
        public IReadOnlyList<IMergeAssistant> Assistants => inner.Assistants;

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IDisposable Register(IMergeAssistant assistant) => host.Track(inner.Register(assistant));

        public IMergeAssistant? Find(string id) => inner.Find(id);
    }

    private sealed class TrackingEditor(PluginHost host, EditorExtensionRegistry inner) : IEditorExtensionRegistry
    {
        public IReadOnlyList<IEditorDecorationProvider> DecorationProviders => inner.DecorationProviders;

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public IDisposable RegisterDecorationProvider(IEditorDecorationProvider provider) => host.Track(inner.RegisterDecorationProvider(provider));

        public Task<IReadOnlyList<EditorDecoration>> CollectDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken)
            => inner.CollectDecorationsAsync(request, cancellationToken);
    }

    private sealed class TrackingHooks(PluginHost host, MergeLifecycleHookRegistry inner) : IMergeLifecycleHookRegistry
    {
        public IReadOnlyList<IMergeLifecycleHook> Hooks => inner.Hooks;

        public IDisposable Register(IMergeLifecycleHook hook) => host.Track(inner.Register(hook));

        public Task<HookResult> RunBeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default) => inner.RunBeforeResolveAsync(context, cancellationToken);

        public Task RunAfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default) => inner.RunAfterResolveAsync(context, cancellationToken);

        public Task<HookResult> RunBeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default) => inner.RunBeforeSaveAsync(context, cancellationToken);
    }

    /// <summary>Settings view whose event subscriptions are removed when the plugin unloads (so they cannot keep it alive).</summary>
    private sealed class TrackingSettings(PluginHost host, ISettingsStore inner) : ISettingsStore
    {
        public string Namespace => inner.Namespace;

        public IReadOnlyCollection<string> Keys => inner.Keys;

        public event EventHandler<string>? Changed
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                inner.Changed += value;
                host.Track(new Registration(() => inner.Changed -= value));
            }

            remove => inner.Changed -= value;
        }

        public T Get<T>(string key, T defaultValue) => inner.Get(key, defaultValue);

        public bool TryGet<T>(string key, out T? value) => inner.TryGet(key, out value);

        public void Set<T>(string key, T value) => inner.Set(key, value);

        public bool Remove(string key) => inner.Remove(key);

        public ISettingsStore GetSection(string name) => new TrackingSettings(host, inner.GetSection(name));
    }

    private sealed class TrackingFileTypes(PluginHost host, FileTypeService inner) : IFileTypeService
    {
        public string DetectLanguage(string? path, string? firstLine = null) => inner.DetectLanguage(path, firstLine);

        public IDisposable RegisterExtension(string extension, string languageId) => host.Track(inner.RegisterExtension(extension, languageId));
    }
}
