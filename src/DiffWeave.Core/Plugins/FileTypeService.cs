using DiffWeave.Extensibility;

namespace DiffWeave.Core.Plugins;

/// <summary>The host's <see cref="IFileTypeService"/>: a built-in extension table plus registrations.</summary>
public sealed class FileTypeService : IFileTypeService
{
    /// <summary>The id returned for unknown files.</summary>
    public const string PlainText = "plaintext";

    private static readonly Dictionary<string, string> s_builtIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["csx"] = "csharp", ["vb"] = "vb", ["fs"] = "fsharp", ["fsx"] = "fsharp",
        ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["cc"] = "cpp", ["cxx"] = "cpp", ["hpp"] = "cpp", ["hh"] = "cpp",
        ["java"] = "java", ["kt"] = "kotlin", ["kts"] = "kotlin", ["scala"] = "scala", ["go"] = "go", ["rs"] = "rust",
        ["swift"] = "swift", ["m"] = "objective-c", ["mm"] = "objective-cpp",
        ["js"] = "javascript", ["mjs"] = "javascript", ["cjs"] = "javascript", ["jsx"] = "javascriptreact",
        ["ts"] = "typescript", ["tsx"] = "typescriptreact", ["py"] = "python", ["pyw"] = "python", ["rb"] = "ruby",
        ["php"] = "php", ["pl"] = "perl", ["pm"] = "perl", ["lua"] = "lua", ["r"] = "r", ["dart"] = "dart",
        ["sh"] = "shellscript", ["bash"] = "shellscript", ["zsh"] = "shellscript", ["ps1"] = "powershell", ["psm1"] = "powershell",
        ["bat"] = "bat", ["cmd"] = "bat",
        ["json"] = "json", ["jsonc"] = "jsonc", ["xml"] = "xml", ["xaml"] = "xml", ["axaml"] = "xml", ["csproj"] = "xml",
        ["props"] = "xml", ["targets"] = "xml", ["slnx"] = "xml", ["resx"] = "xml", ["svg"] = "xml",
        ["html"] = "html", ["htm"] = "html", ["css"] = "css", ["scss"] = "scss", ["less"] = "less",
        ["yml"] = "yaml", ["yaml"] = "yaml", ["toml"] = "toml", ["ini"] = "ini", ["cfg"] = "ini", ["conf"] = "ini",
        ["md"] = "markdown", ["markdown"] = "markdown", ["rst"] = "restructuredtext", ["tex"] = "latex",
        ["sql"] = "sql", ["txt"] = PlainText, ["log"] = "log", ["diff"] = "diff", ["patch"] = "diff",
        ["gradle"] = "groovy", ["groovy"] = "groovy", ["cmake"] = "cmake",
    };

    private static readonly Dictionary<string, string> s_fileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Makefile"] = "makefile", ["GNUmakefile"] = "makefile", ["Dockerfile"] = "dockerfile",
        ["CMakeLists.txt"] = "cmake", [".gitignore"] = "ignore", [".gitattributes"] = "properties",
        [".editorconfig"] = "ini", ["Gemfile"] = "ruby", ["Rakefile"] = "ruby",
    };

    private readonly Lock _gate = new();
    private readonly List<(string Extension, string LanguageId)> _registered = [];

    /// <inheritdoc/>
    public string DetectLanguage(string? path, string? firstLine = null)
    {
        if (!string.IsNullOrEmpty(path))
        {
            string name = Path.GetFileName(path);
            if (s_fileNames.TryGetValue(name, out string? byName))
            {
                return byName;
            }

            string ext = Path.GetExtension(name).TrimStart('.');
            if (ext.Length > 0)
            {
                lock (_gate)
                {
                    for (int i = _registered.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(_registered[i].Extension, ext, StringComparison.OrdinalIgnoreCase))
                        {
                            return _registered[i].LanguageId;
                        }
                    }
                }

                if (s_builtIn.TryGetValue(ext, out string? byExt))
                {
                    return byExt;
                }
            }
        }

        if (!string.IsNullOrEmpty(firstLine))
        {
            string line = firstLine.TrimStart('﻿').Trim();
            if (line.StartsWith("#!", StringComparison.Ordinal))
            {
                if (line.Contains("python", StringComparison.Ordinal))
                {
                    return "python";
                }

                if (line.Contains("node", StringComparison.Ordinal))
                {
                    return "javascript";
                }

                if (line.Contains("perl", StringComparison.Ordinal))
                {
                    return "perl";
                }

                if (line.Contains("ruby", StringComparison.Ordinal))
                {
                    return "ruby";
                }

                if (line.Contains("pwsh", StringComparison.Ordinal))
                {
                    return "powershell";
                }

                if (line.Contains("sh", StringComparison.Ordinal))
                {
                    return "shellscript";
                }
            }

            if (line.StartsWith("<?xml", StringComparison.Ordinal))
            {
                return "xml";
            }

            if (line.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || line.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                return "html";
            }
        }

        return PlainText;
    }

    /// <inheritdoc/>
    public IDisposable RegisterExtension(string extension, string languageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        var entry = (extension.TrimStart('.'), languageId);
        lock (_gate)
        {
            _registered.Add(entry);
        }

        return new Registration(() =>
        {
            lock (_gate)
            {
                _registered.Remove(entry);
            }
        });
    }
}
