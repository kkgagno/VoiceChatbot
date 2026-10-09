using System.Text;
using VoiceChatbot;
using Xunit;

public class RtfAndHtmlTextTests
{
    [Fact]
    public void RtfKeepsBodyTextAndSkipsTablesInfoAndPictures()
    {
        const string rtf =
            @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Arial;}{\f1\fnil\fcharset2 Symbol;}}" +
            @"{\colortbl;\red0\green0\blue0;}{\stylesheet{\s0 Normal;}}{\info{\title Secret title}{\author Jane}}" +
            @"{\header Page header}{\*\generator Riched20 10.0;}\viewkind4\uc1" + "\r\n" +
            @"\pard\f0\fs22 Townhouse \b closing\b0  notes\par" + "\r\n" +
            @"Price: \'2415,000 \emdash  caf\'e9\line second line\par" +
            @"{\pict\wmetafile8\picw100 0123456789abcdef}" +
            @"Unicode: \u8364?  and \u-3913?\par" +
            @"{\field{\*\fldinst{HYPERLINK ""https://example.com""}}{\fldrslt{\ul HOA site}}}\par" +
            @"\trowd\cellx1000\cellx2000 Room\cell Paint\cell\row" + "\r\n" +
            @"Braces \{ and \} and backslash \\\tab tabbed\par}";

        var text = RtfText.ToPlainText(rtf);

        Assert.Equal(
            "Townhouse closing notes\n" +
            "Price: $15,000 — café\n" +
            "second line\n" +
            "Unicode: \u20AC and \uF0B7\n" +
            "HOA site\n" +
            "Room | Paint\n" +
            "Braces { and } and backslash \\\ttabbed",
            text);
        Assert.DoesNotContain("Secret", text);
        Assert.DoesNotContain("Arial", text);
        Assert.DoesNotContain("Page header", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("0123", text);
    }

    [Fact]
    public void RtfUsesTheFontsCodePageForCyrillicAndSkipsBinData()
    {
        var rtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0\fnil\fcharset0 Arial;}{\f1\fnil\fcharset204 Arial CYR;}}" +
                  @"\f1 \'cf\'f0\'e8\'e2\'e5\'f2\f0  hello {\*\blipuid 123}{\pict\bin4 " + "\u0001\u0002}}\u0003" + @"} world\par}";

        Assert.Equal("Привет hello world", RtfText.ToPlainText(rtf));
    }

    [Fact]
    public void RtfDecodesDoubleByteTextAndUcZero()
    {
        // Shift-JIS "日本" as \'hh pairs in a fcharset128 font, and \uc0 Unicode with no fallback.
        var rtf = @"{\rtf1\ansi{\fonttbl{\f0\fcharset128 MS Mincho;}}\f0 \'93\'fa\'96\'7b \uc0\u26085\u26412 \par}";

        Assert.Equal("日本 日本", RtfText.ToPlainText(rtf));
    }

    [Fact]
    public void NonRtfTextIsReturnedAsIs()
    {
        Assert.Equal("just text", RtfText.ToPlainText("just text"));
        Assert.Equal("", RtfText.ToPlainText(""));
        Assert.True(RtfText.LooksLikeRtf("\uFEFF {\\rtf1 x}"));
        Assert.False(RtfText.LooksLikeRtf("{\"json\": 1}"));
    }

    [Fact]
    public void HtmlKeepsVisibleTextParagraphsListsAndTables()
    {
        const string html = """
            <!DOCTYPE html>
            <html><head><title>HOA Notice</title><style>p { color: red }</style>
            <script>var secret = "x";</script></head>
            <body>
              <!-- hidden comment -->
              <h1>Annual   meeting</h1>
              <p>The meeting is on <b>May&nbsp;3</b> at 7&#160;pm &amp; it is
                 mandatory.</p>
              <ul><li>Budget</li><li>Roof &ndash; repairs</li></ul>
              <table><tr><th>Unit</th><th>Fee</th></tr><tr><td>12B</td><td>$350</td></tr></table>
              <p>Line one<br>Line two</p>
              <pre>  keep
              lines</pre>
              <img src="x.png" alt="Site map">
              <a title="a > b" href="#">link text</a>
            </body></html>
            """;

        var text = HtmlDocumentText.ToPlainText(html);

        Assert.Equal(
            "HOA Notice\n\n" +
            "Annual meeting\n\n" +
            "The meeting is on May 3 at 7 pm & it is mandatory.\n\n" +
            "- Budget\n" +
            "- Roof – repairs\n\n" +
            "Unit | Fee\n" +
            "12B | $350\n\n" +
            "Line one\n" +
            "Line two\n\n" +
            "keep\n" +
            "lines\n\n" +
            "Site map link text",
            text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("hidden", text);
    }

    [Fact]
    public void HtmlCellParagraphsStayOnTheirRowAndNestedDivsDoNotStackBlankLines()
    {
        const string html = """
            <table><tr><td><p>Room</p></td><td><p>Paint</p><p>(two coats)</p></td></tr>
            <tr><td>Kitchen<br>downstairs</td><td>White</td></tr></table>
            <div><div><div>One</div></div></div><div>Two</div>
            <ul><li><p>Keys</p></li><li>Mailbox</li></ul>
            """;

        Assert.Equal(
            "Room | Paint (two coats)\nKitchen downstairs | White\n\nOne\nTwo\n\n- Keys\n- Mailbox",
            HtmlDocumentText.ToPlainText(html));
    }

    [Fact]
    public void HtmlCharsetIsReadFromTheMetaTag()
    {
        var bytes = Encoding.ASCII.GetBytes("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1251\"></head>");
        Assert.Equal("windows-1251", HtmlDocumentText.DetectCharset(bytes));
        Assert.Equal("utf-8", HtmlDocumentText.DetectCharset(Encoding.ASCII.GetBytes("<meta charset=\"utf-8\">")));
        Assert.Null(HtmlDocumentText.DetectCharset(Encoding.ASCII.GetBytes("<html><body>hi</body></html>")));
    }

    [Fact]
    public void TextDecodingHandlesBomsUtf8AndAnsi()
    {
        Assert.Equal("café", TextDecoding.Decode(Encoding.UTF8.GetBytes("café")));
        Assert.Equal("café", TextDecoding.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("café")).ToArray()));
        Assert.Equal("café", TextDecoding.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("café")).ToArray()));
        // Windows-1252 bytes (not valid UTF-8): "café – €5"
        Assert.Equal("café – €5", TextDecoding.Decode(new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x20, 0x96, 0x20, 0x80, 0x35 }));
        // A named charset wins for other code pages.
        Assert.Equal("Привет", TextDecoding.Decode(new byte[] { 0xCF, 0xF0, 0xE8, 0xE2, 0xE5, 0xF2 }, "windows-1251"));
        // "iso-8859-1" labels that really hold UTF-8 are read as UTF-8.
        Assert.Equal("café", TextDecoding.Decode(Encoding.UTF8.GetBytes("café"), "iso-8859-1"));
        Assert.Equal("", TextDecoding.Decode(Array.Empty<byte>()));
        Assert.Null(TextDecoding.GetEncoding("no-such-charset"));
        Assert.Equal(1252, TextDecoding.GetEncoding("cp1252")!.CodePage);
    }
}
