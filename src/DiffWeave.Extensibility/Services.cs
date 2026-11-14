namespace DiffWeave.Extensibility;

/// <summary>The context of a merge lifecycle event.</summary>
/// <param name="Snapshot">The merge at the time of the event.</param>
/// <param name="RegionId">The region being resolved (resolve events only).</param>
/// <param name="Choice">A textual description of the chosen resolution (resolve events only), e.g. "Left", "Custom".</param>
public sealed record MergeLifecycleContext(MergeSnapshot Snapshot, int? RegionId = null, string? Choice = null);

/// <summary>The answer of a hook to a "before" event.</summary>
/// <param name="Proceed">Whether the operation may continue.</param>
/// <param name="Message">Optional message shown to the user (required when vetoing).</param>
public sealed record HookResult(bool Proceed, string? Message = null)
{
    /// <summary>Let the operation continue.</summary>
    public static HookResult Continue { get; } = new(true);

    /// <summary>Veto the operation.</summary>
    public static HookResult Cancel(string message) => new(false, message);
}

/// <summary>
/// Observes (and may veto) merge operations. All methods have default no-op implementations; override what you need.
/// Hooks run on the UI thread's async context; a hook that throws or exceeds the host's timeout is treated as
/// <see cref="HookResult.Continue"/> and logged.
/// </summary>
public interface IMergeLifecycleHook
{
    /// <summary>Unique id.</summary>
    string Id { get; }

    /// <summary>Before a region resolution is applied.</summary>
    Task<HookResult> BeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => Task.FromResult(HookResult.Continue);

    /// <summary>After a region resolution was applied.</summary>
    Task AfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Before the merge output is saved. Vetoing keeps the file unsaved.</summary>
    Task<HookResult> BeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken) => Task.FromResult(HookResult.Continue);
}

/// <summary>Registry of merge lifecycle hooks.</summary>
public interface IMergeLifecycleHookRegistry
{
    /// <summary>All registered hooks.</summary>
    IReadOnlyList<IMergeLifecycleHook> Hooks { get; }

    /// <summary>Registers a hook; dispose the result to remove it.</summary>
    IDisposable Register(IMergeLifecycleHook hook);

    /// <summary>Runs all <see cref="IMergeLifecycleHook.BeforeResolveAsync"/> hooks; the first veto wins. Failures are isolated.</summary>
    Task<HookResult> RunBeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default);

    /// <summary>Runs all <see cref="IMergeLifecycleHook.AfterResolveAsync"/> hooks. Failures are isolated.</summary>
    Task RunAfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default);

    /// <summary>Runs all <see cref="IMergeLifecycleHook.BeforeSaveAsync"/> hooks; the first veto wins. Failures are isolated.</summary>
    Task<HookResult> RunBeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default);
}

/// <summary>Maps files to language ids (lower-case identifiers such as "csharp", "python", "json", "plaintext").</summary>
public interface IFileTypeService
{
    /// <summary>
    /// Detects the language of a file from its path (extension or well-known file name) and, failing that, from the
    /// first line of its content (shebang, XML declaration). Returns "plaintext" when unknown.
    /// </summary>
    string DetectLanguage(string? path, string? firstLine = null);

    /// <summary>Maps an extension (with or without leading dot, case-insensitive) to a language id. Dispose to remove.</summary>
    IDisposable RegisterExtension(string extension, string languageId);
}

/// <summary>
/// A namespaced, typed key/value settings store. Values are serialized as JSON, so any JSON-serializable type works.
/// Keys are case-sensitive; '/' separates nested namespaces.
/// </summary>
public interface ISettingsStore
{
    /// <summary>The namespace of this store ("" for the root).</summary>
    string Namespace { get; }

    /// <summary>The keys directly stored in this namespace.</summary>
    IReadOnlyCollection<string> Keys { get; }

    /// <summary>Raised after a value in this namespace changed; the argument is the key.</summary>
    event EventHandler<string>? Changed;

    /// <summary>Returns the value for <paramref name="key"/>, or <paramref name="defaultValue"/> when missing or not convertible.</summary>
    T Get<T>(string key, T defaultValue);

    /// <summary>Tries to get the value for <paramref name="key"/>.</summary>
    bool TryGet<T>(string key, out T? value);

    /// <summary>Stores a value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Removes a value; returns whether it existed.</summary>
    bool Remove(string key);

    /// <summary>Returns a store for a nested namespace.</summary>
    ISettingsStore GetSection(string name);
}
