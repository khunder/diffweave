using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>Disposable that runs an action once.</summary>
internal sealed class Registration(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}

/// <summary>Base for thread-safe copy-on-write registries.</summary>
internal static class RegistryHelpers
{
    public static void RaiseSafely(EventHandler? handler, object sender, IPluginLogger logger)
    {
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler h in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                h(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.Error("A registry change handler threw.", ex);
            }
        }
    }
}

/// <summary>The host's <see cref="ICommandRegistry"/> implementation.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Lock _gate = new();
    private readonly IPluginLogger _logger;
    private CommandContribution[] _commands = [];

    /// <summary>Creates a registry.</summary>
    public CommandRegistry(IPluginLogger? logger = null) => _logger = logger ?? NullPluginLogger.Instance;

    /// <inheritdoc/>
    public IReadOnlyList<CommandContribution> Commands => Volatile.Read(ref _commands);

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IDisposable Register(CommandContribution command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            if (_commands.Any(c => c.Id == command.Id))
            {
                throw new InvalidOperationException($"A command with id '{command.Id}' is already registered.");
            }

            _commands = [.. _commands, command];
        }

        RegistryHelpers.RaiseSafely(Changed, this, _logger);
        return new Registration(() =>
        {
            lock (_gate)
            {
                _commands = [.. _commands.Where(c => !ReferenceEquals(c, command))];
            }

            RegistryHelpers.RaiseSafely(Changed, this, _logger);
        });
    }

    /// <summary>Finds a command by id.</summary>
    public CommandContribution? Find(string commandId) => Commands.FirstOrDefault(c => c.Id == commandId);

    /// <inheritdoc/>
    public bool CanExecute(string commandId, CommandContext context)
    {
        CommandContribution? cmd = Find(commandId);
        if (cmd is null)
        {
            return false;
        }

        try
        {
            return cmd.CanExecute?.Invoke(context) ?? true;
        }
        catch (Exception ex)
        {
            _logger.Error($"CanExecute of command '{commandId}' threw.", ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExecuteAsync(string commandId, CommandContext context, CancellationToken cancellationToken = default)
    {
        CommandContribution? cmd = Find(commandId);
        if (cmd is null || !CanExecute(commandId, context))
        {
            return false;
        }

        try
        {
            await cmd.Execute(context, cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Command '{commandId}' failed.", ex);
            return false;
        }
    }
}

/// <summary>The host's <see cref="IMergeAssistantRegistry"/> implementation.</summary>
public sealed class MergeAssistantRegistry : IMergeAssistantRegistry
{
    private readonly Lock _gate = new();
    private readonly IPluginLogger _logger;
    private IMergeAssistant[] _assistants = [];

    /// <summary>Creates a registry.</summary>
    public MergeAssistantRegistry(IPluginLogger? logger = null) => _logger = logger ?? NullPluginLogger.Instance;

    /// <inheritdoc/>
    public IReadOnlyList<IMergeAssistant> Assistants => Volatile.Read(ref _assistants);

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IDisposable Register(IMergeAssistant assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        string id = assistant.Id;
        lock (_gate)
        {
            if (_assistants.Any(a => a.Id == id))
            {
                throw new InvalidOperationException($"A merge assistant with id '{id}' is already registered.");
            }

            _assistants = [.. _assistants, assistant];
        }

        RegistryHelpers.RaiseSafely(Changed, this, _logger);
        return new Registration(() =>
        {
            lock (_gate)
            {
                _assistants = [.. _assistants.Where(a => !ReferenceEquals(a, assistant))];
            }

            RegistryHelpers.RaiseSafely(Changed, this, _logger);
        });
    }

    /// <inheritdoc/>
    public IMergeAssistant? Find(string id) => Assistants.FirstOrDefault(a => a.Id == id);
}

/// <summary>The host's <see cref="IEditorExtensionRegistry"/> implementation.</summary>
public sealed class EditorExtensionRegistry : IEditorExtensionRegistry
{
    private readonly Lock _gate = new();
    private readonly IPluginLogger _logger;
    private IEditorDecorationProvider[] _providers = [];

    /// <summary>Creates a registry.</summary>
    /// <param name="logger">Logger for isolated failures.</param>
    /// <param name="providerTimeout">Maximum time a provider may take in <see cref="CollectDecorationsAsync"/>.</param>
    public EditorExtensionRegistry(IPluginLogger? logger = null, TimeSpan? providerTimeout = null)
    {
        _logger = logger ?? NullPluginLogger.Instance;
        ProviderTimeout = providerTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Maximum time a provider may take.</summary>
    public TimeSpan ProviderTimeout { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IEditorDecorationProvider> DecorationProviders => Volatile.Read(ref _providers);

    /// <inheritdoc/>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public IDisposable RegisterDecorationProvider(IEditorDecorationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _providers = [.. _providers, provider];
        }

        EventHandler onInvalidated = (_, _) => RegistryHelpers.RaiseSafely(Changed, this, _logger);
        provider.Invalidated += onInvalidated;
        RegistryHelpers.RaiseSafely(Changed, this, _logger);
        return new Registration(() =>
        {
            provider.Invalidated -= onInvalidated;
            lock (_gate)
            {
                _providers = [.. _providers.Where(p => !ReferenceEquals(p, provider))];
            }

            RegistryHelpers.RaiseSafely(Changed, this, _logger);
        });
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EditorDecoration>> CollectDecorationsAsync(DecorationRequest request, CancellationToken cancellationToken)
    {
        IEditorDecorationProvider[] providers = Volatile.Read(ref _providers);
        var tasks = providers.Select(p => RunOne(p, request, cancellationToken)).ToArray();
        IReadOnlyList<EditorDecoration>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return [.. results.SelectMany(r => r)];
    }

    private async Task<IReadOnlyList<EditorDecoration>> RunOne(IEditorDecorationProvider provider, DecorationRequest request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProviderTimeout);
        try
        {
            Task<IReadOnlyList<EditorDecoration>> task = Task.Run(() => provider.GetDecorationsAsync(request, cts.Token), cts.Token);
            IReadOnlyList<EditorDecoration> result = await task.WaitAsync(cts.Token).ConfigureAwait(false);
            return result ?? [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Decoration provider '{SafeId(provider)}' failed or timed out.", ex);
            return [];
        }
    }

    private static string SafeId(IEditorDecorationProvider p)
    {
        try
        {
            return p.Id;
        }
        catch (Exception)
        {
            return p.GetType().FullName ?? "?";
        }
    }
}

/// <summary>The host's <see cref="IMergeLifecycleHookRegistry"/> implementation.</summary>
public sealed class MergeLifecycleHookRegistry : IMergeLifecycleHookRegistry
{
    private readonly Lock _gate = new();
    private readonly IPluginLogger _logger;
    private IMergeLifecycleHook[] _hooks = [];

    /// <summary>Creates a registry.</summary>
    /// <param name="logger">Logger for isolated failures.</param>
    /// <param name="hookTimeout">Maximum time a single hook may take.</param>
    public MergeLifecycleHookRegistry(IPluginLogger? logger = null, TimeSpan? hookTimeout = null)
    {
        _logger = logger ?? NullPluginLogger.Instance;
        HookTimeout = hookTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>Maximum time a single hook may take.</summary>
    public TimeSpan HookTimeout { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IMergeLifecycleHook> Hooks => Volatile.Read(ref _hooks);

    /// <inheritdoc/>
    public IDisposable Register(IMergeLifecycleHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_gate)
        {
            _hooks = [.. _hooks, hook];
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _hooks = [.. _hooks.Where(h => !ReferenceEquals(h, hook))];
            }
        });
    }

    /// <inheritdoc/>
    public Task<HookResult> RunBeforeResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default)
        => RunVetoable((h, ct) => h.BeforeResolveAsync(context, ct), cancellationToken);

    /// <inheritdoc/>
    public async Task RunAfterResolveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default)
    {
        foreach (IMergeLifecycleHook hook in Hooks)
        {
            await RunOne(hook, async ct =>
            {
                await hook.AfterResolveAsync(context, ct).ConfigureAwait(true);
                return HookResult.Continue;
            }, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <inheritdoc/>
    public Task<HookResult> RunBeforeSaveAsync(MergeLifecycleContext context, CancellationToken cancellationToken = default)
        => RunVetoable((h, ct) => h.BeforeSaveAsync(context, ct), cancellationToken);

    private async Task<HookResult> RunVetoable(Func<IMergeLifecycleHook, CancellationToken, Task<HookResult>> call, CancellationToken cancellationToken)
    {
        foreach (IMergeLifecycleHook hook in Hooks)
        {
            HookResult result = await RunOne(hook, ct => call(hook, ct), cancellationToken).ConfigureAwait(true);
            if (!result.Proceed)
            {
                return result;
            }
        }

        return HookResult.Continue;
    }

    private async Task<HookResult> RunOne(IMergeLifecycleHook hook, Func<CancellationToken, Task<HookResult>> call, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(HookTimeout);
        try
        {
            HookResult? result = await call(cts.Token).WaitAsync(cts.Token).ConfigureAwait(true);
            return result ?? HookResult.Continue;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Lifecycle hook '{hook.GetType().FullName}' failed or timed out; ignoring it.", ex);
            return HookResult.Continue;
        }
    }
}
