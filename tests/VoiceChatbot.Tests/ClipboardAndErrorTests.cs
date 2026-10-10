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

    [Theory]
    // llama.cpp started without --mmproj (the app's own request error wraps the server's JSON body).
    [InlineData("Response status code does not indicate success: 500 (Internal Server Error). {\"error\":{\"code\":500,\"message\":\"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj\",\"type\":\"server_error\"}}")]
    [InlineData("Backend error: this model is missing data required for image input")]
    [InlineData("Response status code does not indicate success: 400 (Bad Request). Invalid content type. image_url is only supported by certain models.")]
    [InlineData("Model does not support images. Please use a model that does.")]
    [InlineData("gemma-3-1b is not a multimodal model")]
    [InlineData("Image inputs are not supported by this model")]
    public void PictureErrorsFromAnyServerAreExplained(string message)
    {
        Assert.True(FriendlyErrors.IsPicturesNotSupported(message));
        Assert.Equal(FriendlyErrors.BuiltInModelCannotSeePictures, FriendlyErrors.DescribeChatError(new HttpRequestException(message), builtInModel: true));
        Assert.Equal(FriendlyErrors.ModelCannotSeePictures, FriendlyErrors.DescribeChatError(new HttpRequestException(message), builtInModel: false));
        Assert.Contains("Choose AI model...", FriendlyErrors.BuiltInModelCannotSeePictures);
    }

    [Theory]
    [InlineData("Response status code does not indicate success: 500 (Internal Server Error). {\"error\":\"model 'llama3' not found\"}")]
    [InlineData("The model or server does not support tool calling: tools param requires --jinja flag")]
    [InlineData("The request exceeds the available context size, try increasing it")]
    [InlineData("Connection refused (127.0.0.1:8080)")]
    [InlineData("")]
    public void OtherChatErrorsKeepTheirMessage(string message)
    {
        Assert.False(FriendlyErrors.IsPicturesNotSupported(message));
        Assert.Equal(message, FriendlyErrors.DescribeChatError(new InvalidOperationException(message), builtInModel: true));
    }

    [Fact]
    public void APictureErrorInsideAnotherErrorIsFound()
    {
        var ex = new InvalidOperationException("Chat failed", new HttpRequestException("image input is not supported"));
        Assert.Equal(FriendlyErrors.BuiltInModelCannotSeePictures, FriendlyErrors.DescribeChatError(ex, builtInModel: true));
    }
}
