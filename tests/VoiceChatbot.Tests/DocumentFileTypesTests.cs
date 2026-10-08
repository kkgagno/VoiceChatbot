using System.Text;
using VoiceChatbot;
using Xunit;

public class DocumentFileTypesTests
{
    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".csv")]
    [InlineData(".json")]
    [InlineData(".xml")]
    [InlineData(".log")]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    [InlineData(".yaml")]
    [InlineData(".YML")]
    [InlineData(".ini")]
    [InlineData(".py")]
    [InlineData(".cs")]
    public void ScriptsConfigsAndNotesAreReadAsText(string extension)
    {
        Assert.True(DocumentFileTypes.IsTextExtension(extension));
        Assert.Null(DocumentFileTypes.GetUnsupportedTypeMessage(extension));
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData("")]
    [InlineData(null)]
    public void BinaryAndSpecialFormatsAreNotTextExtensions(string? extension)
    {
        Assert.False(DocumentFileTypes.IsTextExtension(extension));
    }

    [Theory]
    [InlineData(".doc", ".docx")]
    [InlineData(".xlsx", "CSV")]
    [InlineData(".XLS", "CSV")]
    [InlineData(".pptx", "PDF")]
    [InlineData(".rtf", ".docx")]
    [InlineData(".png", "Image button")]
    [InlineData(".mp4", "audio or video")]
    [InlineData(".zip", "Extract")]
    public void KnownUnreadableTypesGetAdviceThatFits(string extension, string expectedHint)
    {
        var message = DocumentFileTypes.GetUnsupportedTypeMessage(extension);

        Assert.NotNull(message);
        Assert.Contains(expectedHint, message);
        Assert.DoesNotContain("scanned", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownBinaryTypeNamesTheExtensionAndNotOcr()
    {
        var message = DocumentFileTypes.DescribeUnsupportedType(".DAT");

        Assert.Contains(".dat", message);
        Assert.DoesNotContain("scanned", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without an extension", DocumentFileTypes.DescribeUnsupportedType(""));
    }

    [Fact]
    public void TextSniffAcceptsPlainUtf8AndBomFiles()
    {
        Assert.True(DocumentFileTypes.LooksLikeText(Encoding.UTF8.GetBytes("name: value\r\n\tlist:\n  - one\n  - café\n")));
        Assert.True(DocumentFileTypes.LooksLikeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("x")).ToArray()));
        // UTF-16 text (with its BOM) is full of NUL bytes but is still text.
        Assert.True(DocumentFileTypes.LooksLikeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Windows Registry Editor")).ToArray()));
        Assert.True(DocumentFileTypes.LooksLikeText(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TextSniffRejectsBinaryData()
    {
        Assert.False(DocumentFileTypes.LooksLikeText(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 }));

        var noisy = Enumerable.Range(0, 200).Select(i => (byte)(i % 3 == 0 ? 0x01 : 0x41)).ToArray();
        Assert.False(DocumentFileTypes.LooksLikeText(noisy));
    }

    [Fact]
    public void OcrNoticeSaysHowManyPagesWereReadOfHowMany()
    {
        var notice = DocumentFileTypes.BuildOcrPageNotice(8, 30, 8);

        Assert.Contains("first 8 of 30 pages", notice);
        Assert.Contains("limited to 8 pages", notice);
        Assert.Contains("Pages 9-30 were not read", notice);
    }

    [Fact]
    public void OcrNoticeHandlesOneSkippedPageAndUnknownTotals()
    {
        Assert.Contains("Page 9 was not read", DocumentFileTypes.BuildOcrPageNotice(8, 9, 8));
        Assert.Contains("Any later pages were not read", DocumentFileTypes.BuildOcrPageNotice(8, 0, 8));
    }

    [Fact]
    public void OcrNoticeForAFullyReadScan()
    {
        Assert.Equal("Scanned PDF: OCR read all 3 pages.", DocumentFileTypes.BuildOcrPageNotice(3, 3, 8));
        Assert.Equal("Scanned PDF: OCR read its 1 page.", DocumentFileTypes.BuildOcrPageNotice(1, 1, 8));
        Assert.Equal("Scanned PDF: OCR read all 3 pages.", DocumentFileTypes.BuildOcrPageNotice(3, 0, 8));
        Assert.Equal("", DocumentFileTypes.BuildOcrPageNotice(0, 12, 8));
    }
}
