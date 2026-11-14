using System.Text;

namespace DiffWeave.Core.Text;

/// <summary>The result of <see cref="EncodingDetector.Detect"/>.</summary>
/// <param name="Encoding">The detected encoding (its preamble is never emitted by <see cref="Encoding.GetBytes(string)"/>).</param>
/// <param name="HasBom">Whether the bytes start with the encoding's byte-order mark.</param>
/// <param name="BomLength">Length of the byte-order mark in bytes (0 when none).</param>
public readonly record struct DetectedEncoding(Encoding Encoding, bool HasBom, int BomLength);

/// <summary>
/// Detects the encoding of text files: byte-order marks (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE), then valid UTF-8,
/// then BOM-less UTF-16 (by the distribution of zero bytes), falling back to Latin-1 (ISO-8859-1), which can decode
/// any byte sequence and re-encode it losslessly.
/// </summary>
public static class EncodingDetector
{
    /// <summary>UTF-8 without BOM emission.</summary>
    public static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-16 LE without BOM emission.</summary>
    public static Encoding Utf16LE { get; } = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>UTF-16 BE without BOM emission.</summary>
    public static Encoding Utf16BE { get; } = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>UTF-32 LE without BOM emission.</summary>
    public static Encoding Utf32LE { get; } = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);

    /// <summary>UTF-32 BE without BOM emission.</summary>
    public static Encoding Utf32BE { get; } = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);

    /// <summary>Latin-1 (ISO-8859-1).</summary>
    public static Encoding Latin1 => Encoding.Latin1;

    /// <summary>Detects the encoding of <paramref name="bytes"/>.</summary>
    public static DetectedEncoding Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return new(Utf32LE, true, 4);
        }

        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return new(Utf32BE, true, 4);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new(Utf8, true, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new(Utf16LE, true, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new(Utf16BE, true, 2);
        }

        if (IsValidUtf8(bytes))
        {
            // UTF-16 text made only of ASCII is also valid UTF-8 (with NULs); check that first.
            Encoding? utf16 = GuessUtf16(bytes);
            return new(utf16 ?? Utf8, false, 0);
        }

        Encoding? guess = GuessUtf16(bytes);
        return new(guess ?? Latin1, false, 0);
    }

    /// <summary>Returns whether the bytes are well-formed UTF-8.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes) => System.Text.Unicode.Utf8.IsValid(bytes);

    /// <summary>Returns whether the bytes look binary (contain NUL bytes and are not UTF-16/32).</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        DetectedEncoding d = Detect(bytes);
        if (d.Encoding is UnicodeEncoding or UTF32Encoding)
        {
            return false;
        }

        int n = Math.Min(bytes.Length, 8000);
        return bytes[..n].Contains((byte)0);
    }

    private static Encoding? GuessUtf16(ReadOnlySpan<byte> bytes)
    {
        int n = Math.Min(bytes.Length, 64 * 1024) & ~1;
        if (n < 2)
        {
            return null;
        }

        int zeroEven = 0, zeroOdd = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (bytes[i] == 0)
            {
                zeroEven++;
            }

            if (bytes[i + 1] == 0)
            {
                zeroOdd++;
            }
        }

        int pairs = n / 2;

        // Mostly-ASCII UTF-16 has a zero in (nearly) every high byte and almost none in the low bytes.
        if (zeroOdd > pairs * 0.4 && zeroEven < pairs * 0.05 && TryDecode(Utf16LE, bytes))
        {
            return Utf16LE;
        }

        if (zeroEven > pairs * 0.4 && zeroOdd < pairs * 0.05 && TryDecode(Utf16BE, bytes))
        {
            return Utf16BE;
        }

        return null;
    }

    private static bool TryDecode(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            _ = encoding.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
