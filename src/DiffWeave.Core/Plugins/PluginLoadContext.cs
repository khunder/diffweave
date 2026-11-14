using System.Reflection;
using System.Runtime.Loader;

namespace DiffWeave.Core.Plugins;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> per plugin. The extension API (and Core) are always taken from the
/// host so that plugin and host agree on the contract types; every other dependency is resolved from the plugin folder.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> s_shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiffWeave.Extensibility",
        "DiffWeave.Core",
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    public PluginLoadContext(string pluginId, string entryAssemblyPath)
        : base("DiffWeave.Plugin." + pluginId, isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        _directory = Path.GetDirectoryName(entryAssemblyPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && s_shared.Contains(assemblyName.Name))
        {
            return null; // fall back to the default context (the host's copy)
        }

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is not null)
        {
            string candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
