using System.Text;
using DiffWeave.Core.Text;

namespace DiffWeave.App.Documents;

/// <summary>An input of a comparison: path, display label and the loaded document (encoding/EOL detected by the core).</summary>
public sealed class InputFile
{
    private InputFile(string? path, string label, TextDocument document, DateTime lastWrite)
    {
        Path = path;
        Label = label;
        Document = document;
        LastWriteTimeUtc = lastWrite;
    }

    /// <summary>Full path, or null for an empty placeholder.</summary>
    public string? Path { get; }

    /// <summary>Display label (from --L1/--L2/--L3 or the file name).</summary>
    public string Label { get; }

    /// <summary>The loaded text.</summary>
    public TextDocument Document { get; }

    /// <summary>Timestamp at load time (used to detect external changes).</summary>
    public DateTime LastWriteTimeUtc { get; }

    /// <summary>The lines.</summary>
    public IReadOnlyList<string> Lines => Document.Lines;

    /// <summary>True when the input is a real file.</summary>
    public bool HasFile => Path is not null;

    /// <summary>Short encoding name.</summary>
    public string EncodingName => DescribeEncoding(Document.Encoding, Document.HasBom);

    /// <summary>Line-ending description.</summary>
    public string LineEndingName => Document.LineCount == 0 ? "LF" : Document.HasMixedLineEndings ? "Mixed" : Document.DominantLineEnding switch
    {
        LineEnding.CRLF => "CRLF",
        LineEnding.CR => "CR",
        _ => "LF",
    };

    /// <summary>"UTF-8 · LF · 120 lines".</summary>
    public string InfoText => HasFile || Document.LineCount > 0
        ? $"{EncodingName} · {LineEndingName} · {Document.LineCount:N0} {(Document.LineCount == 1 ? "line" : "lines")}"
        : "empty";

    /// <summary>Loads a file. A missing path yields an empty document labelled as missing.</summary>
    public static async Task<InputFile> LoadAsync(string path, string? label, CancellationToken cancellationToken = default)
    {
        string full = System.IO.Path.GetFullPath(path);
        TextDocument doc = await TextDocument.LoadAsync(full, null, cancellationToken).ConfigureAwait(false);
        return new InputFile(full, label ?? System.IO.Path.GetFileName(full), doc, File.GetLastWriteTimeUtc(full));
    }

    /// <summary>Loads a file synchronously.</summary>
    public static InputFile Load(string path, string? label)
    {
        string full = System.IO.Path.GetFullPath(path);
        TextDocument doc = TextDocument.Load(full);
        return new InputFile(full, label ?? System.IO.Path.GetFileName(full), doc, File.GetLastWriteTimeUtc(full));
    }

    /// <summary>True for the null device that git passes for the missing side of an added or deleted file.</summary>
    public static bool IsNullDevice(string path) => path is "/dev/null" || string.Equals(path, "NUL", StringComparison.OrdinalIgnoreCase);

    /// <summary>An empty, unnamed input.</summary>
    public static InputFile Empty(string label) => new(null, label, TextDocument.Empty, default);

    /// <summary>An in-memory input (tests, pasted text).</summary>
    public static InputFile FromText(string label, string text, string? path = null) => new(path, label, TextDocument.FromString(text), default);

    /// <summary>True when the file on disk changed since it was loaded.</summary>
    public bool IsModifiedOnDisk()
    {
        try
        {
            return Path is not null && File.Exists(Path) && LastWriteTimeUtc != default && File.GetLastWriteTimeUtc(Path) != LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string DescribeEncoding(Encoding e, bool bom)
    {
        string name = e switch
        {
            UTF8Encoding => "UTF-8",
            UnicodeEncoding u => u.GetPreamble().Length > 0 && u.GetPreamble()[0] == 0xFE ? "UTF-16 BE" : "UTF-16 LE",
            UTF32Encoding => "UTF-32",
            _ when e.CodePage == 28591 => "Latin-1",
            _ when e.CodePage == 1252 => "Windows-1252",
            _ => e.WebName.ToUpperInvariant(),
        };
        if (e is UnicodeEncoding)
        {
            name = e.WebName.Contains("BE", StringComparison.OrdinalIgnoreCase) || e.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE";
        }

        return bom ? name + " BOM" : name;
    }
}
