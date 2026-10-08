using VoiceChatbot;
using Xunit;

public class MarkdownTextTests
{
    // ---------- SplitFencedCode ----------

    [Fact]
    public void Split_ProseOnly()
    {
        var segments = MarkdownText.SplitFencedCode("  Hello **there**.\n\n- one\n- two  \n");
        var segment = Assert.Single(segments);
        Assert.False(segment.IsCode);
        Assert.Equal("Hello **there**.\n\n- one\n- two", segment.Text);
    }

    [Fact]
    public void Split_ProseCodeProse()
    {
        var segments = MarkdownText.SplitFencedCode("Run this:\n\n```powershell\nGet-Process\n```\n\nThen **check** it.");
        Assert.Equal(3, segments.Count);
        Assert.Equal(new ReplySegment(false, "Run this:"), segments[0]);
        Assert.Equal(new ReplySegment(true, "Get-Process", "powershell"), segments[1]);
        Assert.Equal(new ReplySegment(false, "Then **check** it."), segments[2]);
    }

    [Fact]
    public void Split_UnclosedFenceRunsToEnd()
    {
        var segments = MarkdownText.SplitFencedCode("Code:\n```python\nprint('hi')\n");
        Assert.Equal(2, segments.Count);
        Assert.Equal(new ReplySegment(true, "print('hi')", "python"), segments[1]);
    }

    [Fact]
    public void Split_InlineFenceWithoutLanguage()
    {
        var segments = MarkdownText.SplitFencedCode("Try ```print(\"hi\")``` now");
        Assert.Equal(3, segments.Count);
        Assert.Equal(new ReplySegment(true, "print(\"hi\")", ""), segments[1]);
        Assert.Equal("now", segments[2].Text);
    }

    [Fact]
    public void Split_IndentedFenceInListIsDedented()
    {
        var text = "1. Install:\n   ```bash\n   npm install\n     --save\n   ```\n   Then restart.\n2. Done";
        var segments = MarkdownText.SplitFencedCode(text);
        Assert.Equal(3, segments.Count);
        Assert.Equal("1. Install:", segments[0].Text);
        Assert.Equal(new ReplySegment(true, "npm install\n  --save", "bash"), segments[1]);
        Assert.Equal("Then restart.\n2. Done", segments[2].Text);
    }

    [Fact]
    public void Split_EmptyCodeBlockIsSkipped_AndCrLfIsNormalized()
    {
        var segments = MarkdownText.SplitFencedCode("A\r\n```\r\n```\r\nB\r\nC");
        Assert.Equal(2, segments.Count);
        Assert.Equal("A", segments[0].Text);
        Assert.Equal("B\nC", segments[1].Text);
    }

    [Fact]
    public void Split_EmptyInput() => Assert.Empty(MarkdownText.SplitFencedCode(null));

    // ---------- CleanForDisplay ----------

    [Fact]
    public void Display_KeepsMarkdownSyntax()
    {
        var text = "# Title\n\n**Bold** and *italic* and ~~gone~~.\n\n- item\n  - nested\n1. first\n\n> quote\n\n---\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n[link](https://example.com)";
        Assert.Equal(text, MarkdownText.CleanForDisplay(text));
    }

    [Fact]
    public void Display_RemovesEmojisHashtagsAndTags()
    {
        Assert.Equal("Great job! Done.", MarkdownText.CleanForDisplay("Great job! \U0001F389 #winning Done.<unused49>"));
        Assert.Equal("line one\nline two", MarkdownText.CleanForDisplay("line one<br>line two"));
    }

    [Fact]
    public void Display_KeepsAutolinksUrlFragmentsAndCSharp()
    {
        var text = "See <https://example.com/a#part> and C# docs at https://learn.microsoft.com/#top.";
        Assert.Equal(text, MarkdownText.CleanForDisplay(text));
    }

    [Fact]
    public void Display_LeavesCodeAlone()
    {
        var text = "Use `List<string>`  here.\n\n```html\n<div>  #id  \U0001F600</div>\n```";
        Assert.Equal("Use `List<string>` here.\n\n```html\n<div>  #id  \U0001F600</div>\n```", MarkdownText.CleanForDisplay(text));
    }

    [Fact]
    public void Display_SqueezesSpacesButKeepsIndentAndBlankLines()
    {
        Assert.Equal("- a\n    - b c\n\nd", MarkdownText.CleanForDisplay("- a\n    - b  c\n\n\n\nd"));
        Assert.Equal("a b", MarkdownText.CleanForDisplay("a    b"));
    }

    // ---------- ToPlainText ----------

    [Fact]
    public void Plain_StripsSyntax()
    {
        var markdown = "## Plan\n\n**Bold**, *italic*, __strong__, _em_, ~~old~~ and `code`.\n\n- one\n- [x] done\n- [ ] todo\n1. first\n\n> quoted\n\n---\n\nSee [the docs](https://example.com/x_(y)) or ![chart](a.png).";
        var expected = "Plan\n\nBold, italic, strong, em, old and code.\n\n• one\n☑ done\n☐ todo\n1. first\n\nquoted\n\nSee the docs or chart.";
        Assert.Equal(expected, MarkdownText.ToPlainText(markdown));
    }

    [Fact]
    public void Plain_KeepsCodeContentWithoutFences()
    {
        Assert.Equal("Run:\n\nls *.txt", MarkdownText.ToPlainText("Run:\n```bash\nls *.txt\n```"));
    }

    [Fact]
    public void Plain_LeavesOrdinaryAsterisksAndSnakeCase()
    {
        Assert.Equal("5 * 3 = 15 and my_file_name.txt", MarkdownText.ToPlainText("5 * 3 = 15 and my_file_name.txt"));
    }

    [Fact]
    public void Plain_EscapesAndInlineCodeWithMarkdownInside()
    {
        Assert.Equal("Use **kwargs and 2*3", MarkdownText.ToPlainText("Use `**kwargs` and 2\\*3"));
    }

    // ---------- StripForSpeech ----------

    [Fact]
    public void Speech_RemovesMarkersAndCode()
    {
        var markdown = "### Steps\n\n1. **Open** the app\n2. Click [Refresh](https://x.test)\n- [x] Done\n\n```powershell\nGet-Process\n```\n\nThat's it.";
        Assert.Equal("Steps\n\nOpen the app\nClick Refresh\nDone\n\nThat's it.", MarkdownText.StripForSpeech(markdown));
    }

    [Fact]
    public void Speech_DropsTableSeparatorRow()
    {
        Assert.Equal("| a | b |\n| 1 | 2 |", MarkdownText.StripForSpeech("| a | b |\n|:---|---:|\n| 1 | 2 |"));
    }

    // ---------- Links ----------

    [Theory]
    [InlineData("https://example.com/a?b=c", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("www.example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SafeLinks(string? url, bool expected)
    {
        Assert.Equal(expected, MarkdownText.TryGetSafeLinkUri(url, out var uri));
        Assert.Equal(expected, uri is not null);
    }

    [Fact]
    public void SafeLinks_WwwGetsHttps()
    {
        Assert.True(MarkdownText.TryGetSafeLinkUri("www.example.com/page", out var uri));
        Assert.Equal("https://www.example.com/page", uri!.AbsoluteUri);
    }
}
