using System.ComponentModel;
using System.Runtime.InteropServices;
using VoiceChatbot;
using Xunit;

public class ClipboardPastePolicyTests
{
    // What Excel puts on the clipboard for a copied cell range.
    private static readonly string[] ExcelCellFormats =
    [
        "EnhancedMetafile", "MetaFilePict", "Bitmap", "System.Drawing.Bitmap",
        "System.Windows.Media.Imaging.BitmapSource", "Biff12", "Biff8", "HTML Format",
        "Csv", "Text", "UnicodeText", "OEMText", "Rich Text Format"
    ];

    [Fact]
    public void SpreadsheetCellsPasteAsTextNotAPicture()
    {
        Assert.Equal(ClipboardPasteKind.Text, ClipboardPastePolicy.Decide(ExcelCellFormats, "Name\tQty\r\nApples\t3\r\n"));
    }

    [Fact]
    public void BlankCellsStillPasteAsText()
    {
        Assert.Equal(ClipboardPasteKind.Text, ClipboardPastePolicy.Decide(ExcelCellFormats, "\t\r\n"));
    }

    [Theory]
    [InlineData("Bitmap")]
    [InlineData("PNG")]
    [InlineData("DeviceIndependentBitmap")]
    [InlineData("bitmap")]
    public void APictureWithNoTextIsAttached(string format)
    {
        Assert.Equal(ClipboardPasteKind.Image, ClipboardPastePolicy.Decide(new[] { format, "HTML Format" }, null));
        Assert.Equal(ClipboardPasteKind.Image, ClipboardPastePolicy.Decide(new[] { format }, ""));
    }

    [Fact]
    public void NothingSpecialLeavesTheNormalPaste()
    {
        Assert.Equal(ClipboardPasteKind.Nothing, ClipboardPastePolicy.Decide(new[] { "HTML Format", "FileDrop" }, null));
        Assert.Equal(ClipboardPasteKind.Nothing, ClipboardPastePolicy.Decide(null, null));
    }
}

public class FriendlyErrorsTests
{
    [Fact]
    public void LockedClipboardGetsATryAgainMessage()
    {
        var ex = new COMException("OpenClipboard Failed (Exception from HRESULT: 0x800401D0 (CLIPBRD_E_CANT_OPEN))", FriendlyErrors.ClipboardCantOpen);

        Assert.Equal("the clipboard is in use by another app. Try again in a moment.", FriendlyErrors.Describe(ex));
    }

    [Fact]
    public void MissingFileAssociationIsExplained()
    {
        Assert.Equal("no app is set up to open this type of file.", FriendlyErrors.Describe(new Win32Exception(1155)));
    }

    [Fact]
    public void FileErrorsAreExplained()
    {
        Assert.Contains("open in another app", FriendlyErrors.Describe(new IOException("in use", unchecked((int)0x80070020))));
        Assert.Equal("the disk is full.", FriendlyErrors.Describe(new IOException("full", unchecked((int)0x80070070))));
        Assert.StartsWith("access was denied", FriendlyErrors.Describe(new UnauthorizedAccessException("denied")));
        Assert.Equal("the file no longer exists.", FriendlyErrors.Describe(new FileNotFoundException("gone")));
        Assert.Equal("the folder no longer exists.", FriendlyErrors.Describe(new DirectoryNotFoundException("gone")));
    }

    [Fact]
    public void OtherErrorsKeepTheirMessage()
    {
        Assert.Equal("Something odd", FriendlyErrors.Describe(new InvalidOperationException("Something odd")));
        Assert.Equal("Something odd", FriendlyErrors.Describe(new IOException("Something odd")));
    }
}
