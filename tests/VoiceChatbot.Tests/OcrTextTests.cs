using VoiceChatbot;
using Xunit;

public class OcrTextTests
{
    [Fact]
    public void LargeImagesAreScaledDownKeepingTheirShape()
    {
        Assert.Equal((4000u, 3000u), OcrText.FitWithin(4000, 3000, 10000));
        Assert.Equal((2600u, 1950u), OcrText.FitWithin(4000, 3000, 2600));
        Assert.Equal((1300u, 2600u), OcrText.FitWithin(2000, 4000, 2600));
        Assert.Equal((2600u, 1u), OcrText.FitWithin(100000, 10, 2600));
    }

    [Fact]
    public void PdfPagesRenderAtAbout200DpiWithinTheLimit()
    {
        // A US letter page is 816 x 1056 DIPs (8.5 x 11 inches at 96 per inch).
        Assert.Equal((1700u, 2200u), OcrText.PdfRenderSize(816, 1056, 200, 10000));
        Assert.Equal((1600u, 2071u), OcrText.PdfRenderSize(816, 1056, 200, 2071));
        // A poster-size page is scaled to fit.
        var (w, h) = OcrText.PdfRenderSize(96 * 36, 96 * 48, 200, 4000);
        Assert.Equal(4000u, h);
        Assert.Equal(3000u, w);
        // A page without a size is treated as letter size.
        Assert.Equal((1700u, 2200u), OcrText.PdfRenderSize(0, double.NaN, 200, 10000));
    }

    [Fact]
    public void PagesAreJoinedWithPageMarkers()
    {
        Assert.Equal(
            "Page 1\nfirst\n\nPage 3\nthird",
            OcrText.JoinPages(new[] { (3, " third "), (1, "first"), (2, "  ") }));
        Assert.Equal("", OcrText.JoinPages(Array.Empty<(int, string)>()));
    }

    [Fact]
    public void StrayMarksFromPhotosAreNotText()
    {
        Assert.True(OcrText.HasWords("EXIT"));
        Assert.True(OcrText.HasWords("Unit 12B"));
        Assert.False(OcrText.HasWords(""));
        Assert.False(OcrText.HasWords("I ."));
        Assert.False(OcrText.HasWords("- ' ,"));
    }
}
