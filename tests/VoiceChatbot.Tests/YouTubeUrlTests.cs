using VoiceChatbot;
using Xunit;

public class YouTubeUrlTests
{
    private const string Canonical = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&feature=share")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc")]
    [InlineData("https://www.youtube.com/watch?feature=shared&v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ&start_radio=1")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=Xy12&t=42")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/shorts/dQw4w9WgXcQ?feature=share")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?start=10")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("HTTPS://WWW.YOUTUBE.COM/watch?v=dQw4w9WgXcQ")]
    public void RecognizesVideoLinks(string link)
    {
        Assert.True(YouTubeUrl.TryExtract(link, out var url));
        Assert.Equal(Canonical, url);
    }

    [Theory]
    [InlineData("Summarize this video https://m.youtube.com/watch?v=dQw4w9WgXcQ please.")]
    [InlineData("What's in (https://youtu.be/dQw4w9WgXcQ)?")]
    [InlineData("Compare https://example.com/page and https://www.youtube.com/live/dQw4w9WgXcQ.")]
    [InlineData("Summarize:\nhttps://youtu.be/dQw4w9WgXcQ\nthanks")]
    public void FindsLinkInsideText(string text)
    {
        Assert.True(YouTubeUrl.TryExtract(text, out var url));
        Assert.Equal(Canonical, url);
    }

    [Fact]
    public void FirstVideoLinkWins()
    {
        Assert.True(YouTubeUrl.TryExtract(
            "https://www.youtube.com/@veritasium then https://youtu.be/aaaaaaaaaaa and https://youtu.be/bbbbbbbbbbb",
            out var url));
        Assert.Equal("https://www.youtube.com/watch?v=aaaaaaaaaaa", url);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Summarize Veritasium's latest video")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@veritasium")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234567890")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/shorts/")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://vimeo.com/123456789")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQextra")]
    public void IgnoresNonVideoLinks(string? text)
    {
        Assert.False(YouTubeUrl.TryExtract(text, out var url));
        Assert.Equal("", url);
    }
}
