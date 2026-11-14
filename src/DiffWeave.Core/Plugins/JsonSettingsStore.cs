using System.Text.Json;
using System.Text.Json.Nodes;
using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>
/// A thread-safe <see cref="ISettingsStore"/> keeping JSON values in memory, optionally persisted to a JSON file
/// (a flat object whose property names are full keys such as "plugins/my.plugin/threshold").
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Root _root;
    private readonly string _prefix;

    /// <summary>Creates an in-memory store (no file).</summary>
    public JsonSettingsStore()
        : this(new Root(null), string.Empty)
    {
    }

    private JsonSettingsStore(Root root, string ns)
    {
        _root = root;
        Namespace = ns;
        _prefix = ns.Length == 0 ? string.Empty : ns + "/";
    }

    /// <inheritdoc/>
    public string Namespace { get; }

    /// <summary>The backing file, if any.</summary>
    public string? FilePath => _root.FilePath;

    /// <inheritdoc/>
    public event EventHandler<string>? Changed
    {
        add
        {
            lock (_root.Gate)
            {
                _root.Handlers.Add((_prefix, value!));
            }
        }

        remove
        {
            lock (_root.Gate)
            {
                _root.Handlers.Remove((_prefix, value!));
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (_root.Gate)
            {
                return [.. _root.Values.Keys
                    .Where(k => k.StartsWith(_prefix, StringComparison.Ordinal) && k.IndexOf('/', _prefix.Length) < 0)
                    .Select(k => k[_prefix.Length..])];
            }
        }
    }

    /// <summary>Loads a store from a JSON file; a missing or corrupt file yields an empty store bound to that path.</summary>
    public static JsonSettingsStore Load(string filePath)
    {
        var root = new Root(filePath);
        try
        {
            if (File.Exists(filePath) && JsonNode.Parse(File.ReadAllText(filePath)) is JsonObject obj)
            {
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    root.Values[kv.Key] = kv.Value?.DeepClone();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start empty; the file is rewritten on the next Save.
        }

        return new JsonSettingsStore(root, string.Empty);
    }

    /// <summary>Writes all values to <see cref="FilePath"/> (no-op for in-memory stores).</summary>
    public void Save()
    {
        if (_root.FilePath is null)
        {
            return;
        }

        var obj = new JsonObject();
        lock (_root.Gate)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in _root.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                obj[kv.Key] = kv.Value?.DeepClone();
            }
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(_root.FilePath));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(_root.FilePath, obj.ToJsonString(s_json));
    }

    /// <inheritdoc/>
    public T Get<T>(string key, T defaultValue) => TryGet(key, out T? value) ? value! : defaultValue;

    /// <inheritdoc/>
    public bool TryGet<T>(string key, out T? value)
    {
        ValidateKey(key);
        JsonNode? node;
        lock (_root.Gate)
        {
            if (!_root.Values.TryGetValue(_prefix + key, out node))
            {
                value = default;
                return false;
            }

            node = node?.DeepClone();
        }

        try
        {
            value = node is null ? default : node.Deserialize<T>(s_json);
            return node is null ? default(T) is null : true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            value = default;
            return false;
        }
    }

    /// <inheritdoc/>
    public void Set<T>(string key, T value)
    {
        ValidateKey(key);
        JsonNode? node = JsonSerializer.SerializeToNode(value, s_json);
        lock (_root.Gate)
        {
            _root.Values[_prefix + key] = node;
        }

        Raise(key);
    }

    /// <inheritdoc/>
    public bool Remove(string key)
    {
        ValidateKey(key);
        bool removed;
        lock (_root.Gate)
        {
            removed = _root.Values.Remove(_prefix + key);
        }

        if (removed)
        {
            Raise(key);
        }

        return removed;
    }

    /// <inheritdoc/>
    public ISettingsStore GetSection(string name)
    {
        ValidateKey(name);
        return new JsonSettingsStore(_root, _prefix + name);
    }

    private void Raise(string key)
    {
        (string Prefix, EventHandler<string> Handler)[] handlers;
        lock (_root.Gate)
        {
            handlers = [.. _root.Handlers.Where(h => h.Prefix == _prefix)];
        }

        foreach ((string _, EventHandler<string> handler) in handlers)
        {
            try
            {
                handler(this, key);
            }
            catch (Exception)
            {
                // Settings listeners must not break writers.
            }
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("Keys must be non-empty and must not contain '/'.", nameof(key));
        }
    }

    private sealed class Root(string? filePath)
    {
        public Lock Gate { get; } = new();

        public string? FilePath { get; } = filePath;

        public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);

        public List<(string Prefix, EventHandler<string> Handler)> Handlers { get; } = [];
    }
}
