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
    [InlineData(".mp4", "audio or video")]
    [InlineData(".MP3", "audio or video")]
    [InlineData(".zip", "Extract")]
    [InlineData(".pages", "Export it as PDF")]
    [InlineData(".ico", "icon")]
    public void KnownUnreadableTypesGetAdviceThatFits(string extension, string expectedHint)
    {
        var message = DocumentFileTypes.GetUnsupportedTypeMessage(extension);

        Assert.NotNull(message);
        Assert.Contains(expectedHint, message);
        Assert.DoesNotContain("scanned", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DocumentReader.Unsupported, DocumentFileTypes.GetReader(extension));
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".xlsx")]
    [InlineData(".XLS")]
    [InlineData(".pptx")]
    [InlineData(".ppt")]
    [InlineData(".rtf")]
    [InlineData(".odt")]
    [InlineData(".eml")]
    [InlineData(".msg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    [InlineData(".html")]
    public void NowReadableTypesAreNotCalledUnsupported(string extension)
    {
        Assert.Null(DocumentFileTypes.GetUnsupportedTypeMessage(extension));
        Assert.True(DocumentFileTypes.CanRead(extension));
    }

    [Theory]
    [InlineData("report.pdf", DocumentReader.Pdf)]
    [InlineData("Letter.DOCX", DocumentReader.Word)]
    [InlineData("old.doc", DocumentReader.OldWord)]
    [InlineData("budget.xlsm", DocumentReader.Excel)]
    [InlineData("budget.xls", DocumentReader.WindowsFilter)]
    [InlineData("deck.pptx", DocumentReader.PowerPoint)]
    [InlineData("deck.ppt", DocumentReader.WindowsFilter)]
    [InlineData("notes.odt", DocumentReader.OpenDocument)]
    [InlineData("sheet.ods", DocumentReader.OpenDocument)]
    [InlineData("memo.rtf", DocumentReader.Rtf)]
    [InlineData("page.htm", DocumentReader.Html)]
    [InlineData("mail.eml", DocumentReader.Email)]
    [InlineData("saved.mht", DocumentReader.Email)]
    [InlineData("mail.msg", DocumentReader.OutlookMessage)]
    [InlineData("scan.TIFF", DocumentReader.Image)]
    [InlineData("photo.heic", DocumentReader.Image)]
    [InlineData("flyer.pub", DocumentReader.WindowsFilter)]
    [InlineData("notes.txt", DocumentReader.Text)]
    [InlineData("setup.ps1", DocumentReader.Text)]
    [InlineData("data.dat", DocumentReader.Unknown)]
    [InlineData("README", DocumentReader.Unknown)]
    public void EachTypeGetsTheRightReader(string path, DocumentReader expected)
    {
        Assert.Equal(expected, DocumentFileTypes.GetReader(path));
    }

    [Fact]
    public void KnowledgeExtensionsCoverAHomeDocumentsFolder()
    {
        foreach (var ext in new[]
                 {
                     ".pdf", ".docx", ".doc", ".rtf", ".odt", ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".log",
                     ".html", ".htm", ".eml", ".msg", ".xlsx", ".xlsm", ".xls", ".ods", ".pptx", ".ppt", ".odp",
                     ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic"
                 })
        {
            Assert.Contains(ext, DocumentFileTypes.KnowledgeExtensions);
            Assert.True(DocumentFileTypes.IsKnowledgeExtension(ext.ToUpperInvariant()), ext);
            Assert.True(DocumentFileTypes.CanRead(ext), ext);
        }

        Assert.False(DocumentFileTypes.IsKnowledgeExtension(".mp4"));
        Assert.False(DocumentFileTypes.IsKnowledgeExtension(".zip"));
        Assert.False(DocumentFileTypes.IsKnowledgeExtension(""));
        Assert.False(DocumentFileTypes.IsKnowledgeExtension(null));
        Assert.Equal(DocumentFileTypes.KnowledgeExtensions.Count, DocumentFileTypes.KnowledgeExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(DocumentFileTypes.KnowledgeExtensions, e => Assert.Equal(e.ToLowerInvariant(), e));
    }

    [Theory]
    [InlineData(@"C:\Docs\HOA rules.pdf", "PDF")]
    [InlineData("budget.XLSX", "Excel")]
    [InlineData(".xls", "old Excel")]
    [InlineData("letter.doc", "old Word")]
    [InlineData("letter.docx", "Word")]
    [InlineData("deck.ppt", "old PowerPoint")]
    [InlineData("IMG_0042.JPG", "image")]
    [InlineData("photo.heic", "image")]
    [InlineData("mail.eml", "email")]
    [InlineData("mail.msg", "Outlook email")]
    [InlineData("page.html", "web page")]
    [InlineData("notes.txt", "text")]
    [InlineData("data.dat", "DAT")]
    [InlineData("README", "file")]
    [InlineData(null, "file")]
    public void TypeNamesForSummaries(string? path, string expected)
    {
        Assert.Equal(expected, DocumentFileTypes.GetTypeName(path));
    }

    [Fact]
    public void TypeCountsListTheMostCommonTypesFirst()
    {
        var paths = new[] { "a.pdf", "b.pdf", "c.PDF", "d.jpg", "e.png", "f.xlsx", "g.docx", "h.docx" };

        Assert.Equal("3 PDF, 2 image, 2 Word, 1 Excel", DocumentFileTypes.DescribeTypeCounts(paths));
        Assert.Equal("3 PDF, 2 image, 3 other", DocumentFileTypes.DescribeTypeCounts(paths, maxTypes: 2));
        Assert.Equal("", DocumentFileTypes.DescribeTypeCounts(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(".doc", "Old Word format (.doc) and no Windows text filter is installed: save it as .docx or install the Microsoft Office filter pack.")]
    [InlineData(".xls", "Old Excel format (.xls)")]
    [InlineData(".ppt", "Old PowerPoint format (.ppt)")]
    [InlineData(".pub", "Publisher")]
    [InlineData(".wpd", "WordPerfect")]
    [InlineData(".msg", "Outlook")]
    [InlineData(".dat", "Can't read .dat files")]
    public void NoFilterMessagesNameTheFormatAndTheFix(string extension, string expected)
    {
        Assert.Contains(expected, DocumentFileTypes.GetNoFilterMessage(extension));
    }

    [Fact]
    public void CloudOnlyAttributesAreRecognized()
    {
        Assert.True(DocumentFileTypes.IsCloudOnly((FileAttributes)0x400000 | FileAttributes.Archive));
        Assert.True(DocumentFileTypes.IsCloudOnly((FileAttributes)0x40000));
        Assert.True(DocumentFileTypes.IsCloudOnly(FileAttributes.Offline));
        Assert.False(DocumentFileTypes.IsCloudOnly(FileAttributes.Archive | FileAttributes.ReadOnly));
        // Pinned ("Always keep on this device") files are local.
        Assert.False(DocumentFileTypes.IsCloudOnly((FileAttributes)0x80000 | FileAttributes.Archive));
        Assert.Contains("Always keep on this device", DocumentFileTypes.OneDriveOnlineOnlyMessage);
    }

    [Fact]
    public void ImageAndOcrMessagesSayWhatToInstall()
    {
        Assert.Contains("HEIF Image Extensions", DocumentFileTypes.GetImageDecodeMessage(".heic"));
        Assert.Contains("Webp Image Extensions", DocumentFileTypes.GetImageDecodeMessage(".webp"));
        Assert.Contains("damaged", DocumentFileTypes.GetImageDecodeMessage(".png"));
        Assert.Contains("Settings > Time & language > Language", DocumentFileTypes.OcrLanguageMissingMessage);
        Assert.Contains("password-protected", DocumentFileTypes.GetPasswordMessage("a.xlsx"));
        Assert.Contains("Excel", DocumentFileTypes.GetPasswordMessage("a.xlsx"));
    }

    [Fact]
    public void SniffRecognizesFormatsByTheirFirstBytes()
    {
        Assert.Equal(SniffedFormat.Pdf, DocumentFileTypes.Sniff(Encoding.ASCII.GetBytes("%PDF-1.7\n")));
        Assert.Equal(SniffedFormat.Zip, DocumentFileTypes.Sniff(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }));
        Assert.Equal(SniffedFormat.CompoundFile, DocumentFileTypes.Sniff(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0 }));
        Assert.Equal(SniffedFormat.Rtf, DocumentFileTypes.Sniff(Encoding.ASCII.GetBytes("  {\\rtf1\\ansi hello}")));
        Assert.Equal(SniffedFormat.Html, DocumentFileTypes.Sniff(Encoding.ASCII.GetBytes("\r\n<!DOCTYPE html><html>")));
        Assert.Equal(SniffedFormat.Html, DocumentFileTypes.Sniff(Encoding.ASCII.GetBytes("<HTML><body>")));
        Assert.Equal(SniffedFormat.Unknown, DocumentFileTypes.Sniff(Encoding.ASCII.GetBytes("Name,Amount")));
        Assert.Equal(SniffedFormat.Unknown, DocumentFileTypes.Sniff(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void PdfPagesWithoutATextLayerAreOcrd()
    {
        Assert.True(DocumentFileTypes.PdfPageNeedsOcr(""));
        Assert.True(DocumentFileTypes.PdfPageNeedsOcr("Scanned by CamScanner"));
        Assert.True(DocumentFileTypes.PdfPageNeedsOcr("Page 3 of 12"));
        Assert.False(DocumentFileTypes.PdfPageNeedsOcr("The seller agrees to repair the roof before the closing date of June 1."));
    }

    [Fact]
    public void ScannedPagesNoticeForMixedPdfs()
    {
        Assert.Equal("OCR read 3 scanned pages.", DocumentFileTypes.BuildScannedPagesNotice(3, Array.Empty<int>()));
        Assert.Equal("OCR read 1 scanned page.", DocumentFileTypes.BuildScannedPagesNotice(1, Array.Empty<int>()));
        Assert.Equal(
            "OCR read 8 scanned pages (OCR is limited to 8 pages). Scanned pages 12-14 and 20 were not read.",
            DocumentFileTypes.BuildScannedPagesNotice(8, new[] { 12, 13, 14, 20 }, 8));
        Assert.Equal(
            "OCR read 8 scanned pages (OCR is limited to 8 pages). Scanned page 30 was not read.",
            DocumentFileTypes.BuildScannedPagesNotice(8, new[] { 30 }, 8));
        Assert.Equal("", DocumentFileTypes.BuildScannedPagesNotice(0, Array.Empty<int>()));
    }

    [Fact]
    public void PageRangesAreCompact()
    {
        Assert.Equal("3-5, 9 and 12", DocumentFileTypes.FormatPageRanges(new[] { 9, 3, 4, 5, 12, 4 }));
        Assert.Equal("7", DocumentFileTypes.FormatPageRanges(new[] { 7 }));
        Assert.Equal("1-3", DocumentFileTypes.FormatPageRanges(new[] { 1, 2, 3 }));
        Assert.Equal("", DocumentFileTypes.FormatPageRanges(Array.Empty<int>()));
    }

    [Fact]
    public void OpenFileDialogFilterListsDocumentsAndPictures()
    {
        var parts = DocumentFileTypes.BuildOpenFileDialogFilter().Split('|');

        Assert.Equal(6, parts.Length);
        Assert.Contains("*.pdf", parts[1]);
        Assert.Contains("*.xlsx", parts[1]);
        Assert.DoesNotContain("*.jpg", parts[1]);
        Assert.Contains("*.jpg", parts[3]);
        Assert.Equal("*.*", parts[5]);
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
    public void OcrPageLimitIn_ReadsTheLimitFromANoticeAtTheStart()
    {
        Assert.Equal(8, DocumentFileTypes.OcrPageLimitIn($"[{DocumentFileTypes.BuildOcrPageNotice(8, 30, 8)}]\n\nPage 1 text"));
        Assert.Equal(8, DocumentFileTypes.OcrPageLimitIn($"[{DocumentFileTypes.BuildScannedPagesNotice(8, new[] { 12, 13 }, 8)}]\n\nText"));
        Assert.Equal(200, DocumentFileTypes.OcrPageLimitIn($"[{DocumentFileTypes.BuildOcrPageNotice(200, 450, 200)}] Text"));
        Assert.Equal(8, DocumentFileTypes.OcrPageLimitIn("[OCR read the first 8 of 20 pages of this image (OCR is limited to 8 pages).]\n\nText"));
        // Every page read, no notice, or the words somewhere in the document itself.
        Assert.Equal(0, DocumentFileTypes.OcrPageLimitIn($"[{DocumentFileTypes.BuildOcrPageNotice(5, 5, 8)}]\n\nText"));
        Assert.Equal(0, DocumentFileTypes.OcrPageLimitIn("Plain text"));
        Assert.Equal(0, DocumentFileTypes.OcrPageLimitIn("[Draft] Our scanner (OCR is limited to 8 pages) is slow."));
        Assert.Equal(0, DocumentFileTypes.OcrPageLimitIn(null));
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
