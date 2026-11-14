using System.Text;

namespace DiffWeave.Core.Text;

/// <summary>
/// An immutable text file: its lines (without terminators), the terminator of every line, its encoding and whether it
/// had a byte-order mark. <see cref="ToBytes"/> reproduces the original bytes exactly for any file that was decoded
/// without loss (all files read with a detected encoding are).
/// </summary>
/// <remarks>
/// Line model: a terminator ends a line, it does not start a new one. "" has 0 lines, "a" has 1 line without
/// terminator, "a\n" has 1 line terminated by LF, "a\n\n" has 2 lines ("a" and ""). Only the last line can have
/// <see cref="LineEnding.None"/>; <see cref="HasFinalNewline"/> tells whether it is terminated.
/// </remarks>
public sealed class TextDocument
{
    private readonly string[] _lines;
    private readonly LineEnding[] _endings;

    private TextDocument(string[] lines, LineEnding[] endings, Encoding encoding, bool hasBom)
    {
        _lines = lines;
        _endings = endings;
        Encoding = encoding;
        HasBom = hasBom;
        DominantLineEnding = ComputeDominant(endings, out bool mixed);
        HasMixedLineEndings = mixed;
    }

    /// <summary>An empty UTF-8 document.</summary>
    public static TextDocument Empty { get; } = new([], [], EncodingDetector.Utf8, false);

    /// <summary>The lines, without terminators.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Number of lines.</summary>
    public int LineCount => _lines.Length;

    /// <summary>The encoding used to read the file and used by <see cref="ToBytes"/>.</summary>
    public Encoding Encoding { get; }

    /// <summary>Whether the file starts with a byte-order mark (re-emitted by <see cref="ToBytes"/>).</summary>
    public bool HasBom { get; }

    /// <summary>
    /// The most frequent terminator; ties prefer LF, then CRLF, then CR. For a document without terminators it is
    /// the platform default.
    /// </summary>
    public LineEnding DominantLineEnding { get; }

    /// <summary>Whether lines use more than one kind of terminator.</summary>
    public bool HasMixedLineEndings { get; }

    /// <summary>Whether the last line is terminated (false for empty documents).</summary>
    public bool HasFinalNewline => _endings.Length > 0 && _endings[^1] != LineEnding.None;

    /// <summary>The terminator of line <paramref name="line"/>.</summary>
    public LineEnding GetLineEnding(int line) => _endings[line];

    /// <summary>All terminators, one per line.</summary>
    public IReadOnlyList<LineEnding> LineEndingsList => _endings;

    /// <summary>Creates a document from text.</summary>
    /// <param name="text">The full text.</param>
    /// <param name="encoding">The encoding to use when saving; UTF-8 when <see langword="null"/>.</param>
    /// <param name="hasBom">Whether to write a byte-order mark when saving.</param>
    public static TextDocument FromString(string text, Encoding? encoding = null, bool hasBom = false)
    {
        var endings = new List<LineEnding>();
        List<string> lines = LineEndings.Split(text, endings);
        return new TextDocument([.. lines], [.. endings], encoding ?? EncodingDetector.Utf8, hasBom);
    }

    /// <summary>
    /// Creates a document from lines, terminating every line with <paramref name="lineEnding"/> except possibly the last.
    /// </summary>
    public static TextDocument FromLines(IEnumerable<string> lines, LineEnding lineEnding, bool finalNewline, Encoding? encoding = null, bool hasBom = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lineEnding == LineEnding.None)
        {
            throw new ArgumentException("A line ending is required.", nameof(lineEnding));
        }

        string[] arr = [.. lines];
        var endings = new LineEnding[arr.Length];
        Array.Fill(endings, lineEnding);
        if (arr.Length > 0 && !finalNewline)
        {
            endings[^1] = LineEnding.None;
        }

        return new TextDocument(arr, endings, encoding ?? EncodingDetector.Utf8, hasBom);
    }

    /// <summary>
    /// Returns a document with new content that keeps this document's encoding, BOM, dominant line ending and
    /// final-newline state (an empty source document yields a final newline).
    /// </summary>
    public TextDocument WithLines(IEnumerable<string> lines)
        => FromLines(lines, DominantLineEnding, LineCount == 0 || HasFinalNewline, Encoding, HasBom);

    /// <summary>Decodes bytes, detecting the encoding unless <paramref name="encoding"/> is given.</summary>
    /// <param name="bytes">The raw file content.</param>
    /// <param name="encoding">Forces an encoding; a matching BOM is still recognised and stripped.</param>
    public static TextDocument FromBytes(ReadOnlySpan<byte> bytes, Encoding? encoding = null)
    {
        DetectedEncoding detected = EncodingDetector.Detect(bytes);
        Encoding enc;
        int skip;
        bool bom;
        if (encoding is null)
        {
            enc = detected.Encoding;
            skip = detected.BomLength;
            bom = detected.HasBom;
        }
        else
        {
            enc = encoding;
            bom = detected.HasBom && detected.Encoding.CodePage == encoding.CodePage;
            skip = bom ? detected.BomLength : 0;
        }

        string text;
        try
        {
            text = enc.GetString(bytes[skip..]);
        }
        catch (DecoderFallbackException) when (encoding is null)
        {
            enc = EncodingDetector.Latin1;
            bom = false;
            text = enc.GetString(bytes);
        }

        return FromString(text, enc, bom);
    }

    /// <summary>Reads a file, detecting its encoding unless <paramref name="encoding"/> is given.</summary>
    public static TextDocument Load(string path, Encoding? encoding = null) => FromBytes(File.ReadAllBytes(path), encoding);

    /// <summary>Reads a file asynchronously.</summary>
    public static async Task<TextDocument> LoadAsync(string path, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return FromBytes(bytes, encoding);
    }

    /// <summary>The text with the original terminators (no BOM).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Length; i++)
        {
            sb.Append(_lines[i]).Append(_endings[i].ToText());
        }

        return sb.ToString();
    }

    /// <summary>The encoded content including the BOM when <see cref="HasBom"/>.</summary>
    public byte[] ToBytes()
    {
        byte[] body = Encoding.GetBytes(ToText());
        if (!HasBom)
        {
            return body;
        }

        byte[] bom = Bom(Encoding);
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    /// <summary>Writes the document to <paramref name="path"/> (via a temporary file that replaces the target).</summary>
    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        string tmp = Path.Combine(Path.GetDirectoryName(full) ?? ".", "." + Path.GetFileName(full) + ".diffweave-tmp");
        File.WriteAllBytes(tmp, ToBytes());
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Writes the document asynchronously.</summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(path);
        string tmp = Path.Combine(Path.GetDirectoryName(full) ?? ".", "." + Path.GetFileName(full) + ".diffweave-tmp");
        await File.WriteAllBytesAsync(tmp, ToBytes(), cancellationToken).ConfigureAwait(false);
        File.Move(tmp, full, overwrite: true);
    }

    private static byte[] Bom(Encoding encoding) => encoding switch
    {
        UTF8Encoding => [0xEF, 0xBB, 0xBF],
        UnicodeEncoding u when u.CodePage == 1201 => [0xFE, 0xFF],
        UnicodeEncoding => [0xFF, 0xFE],
        UTF32Encoding u when u.CodePage == 12001 => [0, 0, 0xFE, 0xFF],
        UTF32Encoding => [0xFF, 0xFE, 0, 0],
        _ => encoding.GetPreamble(),
    };

    private static LineEnding ComputeDominant(LineEnding[] endings, out bool mixed)
    {
        int lf = 0, crlf = 0, cr = 0;
        foreach (LineEnding e in endings)
        {
            switch (e)
            {
                case LineEnding.LF: lf++; break;
                case LineEnding.CRLF: crlf++; break;
                case LineEnding.CR: cr++; break;
            }
        }

        mixed = (lf > 0 ? 1 : 0) + (crlf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0) > 1;
        if (lf == 0 && crlf == 0 && cr == 0)
        {
            return LineEndings.PlatformDefault;
        }

        if (lf >= crlf && lf >= cr)
        {
            return LineEnding.LF;
        }

        return crlf >= cr ? LineEnding.CRLF : LineEnding.CR;
    }
}
