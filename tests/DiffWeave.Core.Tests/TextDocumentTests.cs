using System.Text;
using AwesomeAssertions;
using DiffWeave.Core.Text;
using NUnit.Framework;

namespace DiffWeave.Core.Tests;

public class TextDocumentTests
{
    [TestCase("", 0, false)]
    [TestCase("a", 1, false)]
    [TestCase("a\n", 1, true)]
    [TestCase("a\n\n", 2, true)]
    [TestCase("\n", 1, true)]
    [TestCase("a\r\nb\rc\nd", 4, false)]
    [TestCase("a\r\r\n", 2, true)]
    public void Splits_lines_and_round_trips(string text, int lines, bool finalNewline)
    {
        TextDocument doc = TextDocument.FromString(text);
        doc.LineCount.Should().Be(lines);
        doc.HasFinalNewline.Should().Be(finalNewline);
        doc.ToText().Should().Be(text);
        doc.Lines.Should().NotContain(l => l.Contains('\n') || l.Contains('\r'));
    }

    [Test]
    public void Reports_line_endings_and_dominant()
    {
        TextDocument doc = TextDocument.FromString("a\r\nb\r\nc\nd");
        doc.GetLineEnding(0).Should().Be(LineEnding.CRLF);
        doc.GetLineEnding(2).Should().Be(LineEnding.LF);
        doc.GetLineEnding(3).Should().Be(LineEnding.None);
        doc.DominantLineEnding.Should().Be(LineEnding.CRLF);
        doc.HasMixedLineEndings.Should().BeTrue();
    }

    public static IEnumerable<TestCaseData> Encodings()
    {
        yield return new TestCaseData(new UTF8Encoding(true), true).SetName("UTF-8 BOM");
        yield return new TestCaseData(new UTF8Encoding(false), false).SetName("UTF-8");
        yield return new TestCaseData(new UnicodeEncoding(false, true), true).SetName("UTF-16LE BOM");
        yield return new TestCaseData(new UnicodeEncoding(true, true), true).SetName("UTF-16BE BOM");
        yield return new TestCaseData(new UnicodeEncoding(false, false), false).SetName("UTF-16LE no BOM");
        yield return new TestCaseData(new UnicodeEncoding(true, false), false).SetName("UTF-16BE no BOM");
        yield return new TestCaseData(new UTF32Encoding(false, true), true).SetName("UTF-32LE BOM");
    }

    [TestCaseSource(nameof(Encodings))]
    public void Detects_encoding_and_round_trips_bytes(Encoding encoding, bool bom)
    {
        string text = "Hello wörld\r\nsecond line\nthird €";
        byte[] bytes = [.. (bom ? encoding.GetPreamble() : []), .. encoding.GetBytes(text)];
        TextDocument doc = TextDocument.FromBytes(bytes);
        doc.HasBom.Should().Be(bom);
        doc.ToText().Should().Be(text);
        doc.ToBytes().Should().Equal(bytes);
        doc.Encoding.CodePage.Should().Be(encoding.CodePage);
    }

    [Test]
    public void Falls_back_to_latin1_for_invalid_utf8()
    {
        byte[] bytes = [0x63, 0x61, 0x66, 0xE9, 0x0A, 0xFF, 0x41]; // "café\n\xFFA" in Latin-1
        TextDocument doc = TextDocument.FromBytes(bytes);
        doc.Encoding.CodePage.Should().Be(28591);
        doc.Lines[0].Should().Be("café");
        doc.ToBytes().Should().Equal(bytes);
    }

    [Test]
    public void Save_and_load_preserve_bytes_exactly()
    {
        using var tmp = new TempDir();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x\r\ny\rz")];
        string path = Path.Combine(tmp.Path, "f.txt");
        File.WriteAllBytes(path, bytes);
        TextDocument doc = TextDocument.Load(path);
        string copy = Path.Combine(tmp.Path, "g.txt");
        doc.Save(copy);
        File.ReadAllBytes(copy).Should().Equal(bytes);
        Directory.GetFiles(tmp.Path).Should().HaveCount(2, "no temporary file must remain");
    }

    [Test]
    public void WithLines_keeps_format()
    {
        TextDocument doc = TextDocument.FromString("a\r\nb", new UnicodeEncoding(false, false), hasBom: true);
        TextDocument edited = doc.WithLines(["x", "y", "z"]);
        edited.ToText().Should().Be("x\r\ny\r\nz");
        edited.HasBom.Should().BeTrue();
        edited.Encoding.CodePage.Should().Be(1200);
        TextDocument.Empty.WithLines(["q"]).HasFinalNewline.Should().BeTrue();
    }

    [Test]
    public void Binary_detection()
    {
        EncodingDetector.LooksBinary([0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFE, 0x81]).Should().BeTrue();
        EncodingDetector.LooksBinary(Encoding.UTF8.GetBytes("plain text\n")).Should().BeFalse();
        EncodingDetector.LooksBinary(Encoding.Unicode.GetBytes("utf16 text\n")).Should().BeFalse();
    }
}
