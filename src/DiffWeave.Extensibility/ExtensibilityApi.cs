namespace DiffWeave.Extensibility;

/// <summary>
/// Version information of the DiffWeave extension API implemented by this assembly.
/// </summary>
/// <remarks>
/// The API follows semantic versioning: the major number changes on breaking changes, the minor number when
/// members are added. A plugin declares the range of API versions it supports in its <c>plugin.json</c>
/// (see <see cref="PluginManifest.ApiVersion"/>); the host refuses to load plugins whose range does not
/// contain <see cref="ApiVersion"/>.
/// </remarks>
public static class ExtensibilityApi
{
    /// <summary>The version of the extension API, as <c>major.minor</c>.</summary>
    public const string ApiVersion = "1.0";

    /// <summary>The parsed form of <see cref="ApiVersion"/>.</summary>
    public static Version Version { get; } = new(ApiVersion);

    /// <summary>The file name of a plugin manifest inside a plugin folder.</summary>
    public const string ManifestFileName = "plugin.json";
}
