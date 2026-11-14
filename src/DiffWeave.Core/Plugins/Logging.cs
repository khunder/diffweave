using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>A log entry.</summary>
/// <param name="Timestamp">When it was written (UTC).</param>
/// <param name="Source">The plugin id, or "host".</param>
/// <param name="Level">Severity.</param>
/// <param name="Message">Text.</param>
/// <param name="Exception">Optional exception.</param>
public sealed record PluginLogEntry(DateTime Timestamp, string Source, PluginLogLevel Level, string Message, Exception? Exception);

/// <summary>
/// A thread-safe ring buffer of log entries (for a "plugin log" view), optionally forwarding to a callback.
/// </summary>
public sealed class PluginLogBuffer
{
    private readonly Lock _gate = new();
    private readonly Queue<PluginLogEntry> _entries = new();
    private readonly Action<PluginLogEntry>? _forward;

    /// <summary>Creates a buffer.</summary>
    /// <param name="capacity">Maximum number of retained entries.</param>
    /// <param name="forward">Optional callback invoked for every entry (e.g. to write to stderr).</param>
    public PluginLogBuffer(int capacity = 1000, Action<PluginLogEntry>? forward = null)
    {
        Capacity = Math.Max(1, capacity);
        _forward = forward;
    }

    /// <summary>Maximum number of retained entries.</summary>
    public int Capacity { get; }

    /// <summary>Raised (on the writing thread) after an entry was added.</summary>
    public event EventHandler<PluginLogEntry>? EntryAdded;

    /// <summary>A copy of the retained entries, oldest first.</summary>
    public IReadOnlyList<PluginLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>Returns a logger that tags entries with <paramref name="source"/>.</summary>
    public IPluginLogger CreateLogger(string source) => new SourceLogger(this, source);

    /// <summary>Adds an entry.</summary>
    public void Add(PluginLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        try
        {
            _forward?.Invoke(entry);
            EntryAdded?.Invoke(this, entry);
        }
        catch (Exception)
        {
            // Logging must never fail the caller.
        }
    }

    private sealed class SourceLogger(PluginLogBuffer buffer, string source) : IPluginLogger
    {
        public void Log(PluginLogLevel level, string message, Exception? exception = null)
            => buffer.Add(new PluginLogEntry(DateTime.UtcNow, source, level, message, exception));
    }
}

/// <summary>A logger that discards everything.</summary>
public sealed class NullPluginLogger : IPluginLogger
{
    /// <summary>The instance.</summary>
    public static NullPluginLogger Instance { get; } = new();

    /// <inheritdoc/>
    public void Log(PluginLogLevel level, string message, Exception? exception = null)
    {
    }
}
