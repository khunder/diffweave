using System.Text.Json;
using System.Text.RegularExpressions;
using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>Reads and validates <c>plugin.json</c> manifests.</summary>
public static partial class PluginManifestReader
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses and validates manifest JSON. Returns the manifest, or <see langword="null"/> with the problems in <paramref name="errors"/>.</summary>
    public static PluginManifest? Parse(string json, out IReadOnlyList<string> errors)
    {
        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(json, s_json);
        }
        catch (JsonException ex)
        {
            errors = [$"Invalid JSON: {ex.Message}"];
            return null;
        }

        if (dto is null)
        {
            errors = ["The manifest is empty."];
            return null;
        }

        var problems = new List<string>();
        Require(dto.Id, "id", problems);
        Require(dto.Name, "name", problems);
        Require(dto.Version, "version", problems);
        Require(dto.ApiVersion, "apiVersion", problems);
        Require(dto.EntryAssembly, "entryAssembly", problems);
        Require(dto.EntryType, "entryType", problems);
        if (problems.Count > 0)
        {
            errors = problems;
            return null;
        }

        var manifest = new PluginManifest
        {
            Id = dto.Id!,
            Name = dto.Name!,
            Version = dto.Version!,
            ApiVersion = dto.ApiVersion!,
            EntryAssembly = dto.EntryAssembly!,
            EntryType = dto.EntryType!,
            Description = dto.Description,
            Authors = dto.Authors ?? [],
        };
        errors = Validate(manifest);
        return errors.Count == 0 ? manifest : null;
    }

    /// <summary>Reads <c>plugin.json</c> from a plugin folder.</summary>
    public static PluginManifest? Read(string pluginDirectory, out IReadOnlyList<string> errors)
    {
        string path = Path.Combine(pluginDirectory, ExtensibilityApi.ManifestFileName);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors = [$"Cannot read {path}: {ex.Message}"];
            return null;
        }

        return Parse(json, out errors);
    }

    /// <summary>Validates the field contents of a manifest.</summary>
    public static IReadOnlyList<string> Validate(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<string>();
        if (!IdRegex().IsMatch(manifest.Id))
        {
            problems.Add($"'id' must consist of letters, digits, '.', '-' and '_' and start with a letter or digit (got '{manifest.Id}').");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            problems.Add("'name' must not be empty.");
        }

        if (!TryParsePluginVersion(manifest.Version, out _))
        {
            problems.Add($"'version' is not a valid version (got '{manifest.Version}').");
        }

        if (!ApiVersionRange.TryParse(manifest.ApiVersion, out _))
        {
            problems.Add($"'apiVersion' is not a valid version range (got '{manifest.ApiVersion}').");
        }

        string asm = manifest.EntryAssembly;
        if (Path.IsPathRooted(asm) || asm.Split('/', '\\').Contains("..") || !asm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"'entryAssembly' must be a relative path to a .dll inside the plugin folder (got '{asm}').");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            problems.Add("'entryType' must not be empty.");
        }

        return problems;
    }

    /// <summary>Parses a plugin version, ignoring a pre-release/build suffix ("1.2.0-beta+abc" → 1.2.0).</summary>
    public static bool TryParsePluginVersion(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string core = text.Split('-', '+')[0].Trim();
        if (!core.Contains('.', StringComparison.Ordinal))
        {
            core += ".0";
        }

        return Version.TryParse(core, out version);
    }

    private static void Require(string? value, string name, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"Missing required field '{name}'.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdRegex();

    private sealed class ManifestDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Version { get; set; }

        public string? ApiVersion { get; set; }

        public string? EntryAssembly { get; set; }

        public string? EntryType { get; set; }

        public string? Description { get; set; }

        public List<string>? Authors { get; set; }
    }
}
