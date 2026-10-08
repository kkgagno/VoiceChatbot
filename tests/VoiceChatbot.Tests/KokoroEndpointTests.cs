using VoiceChatbot;
using Xunit;

public class KokoroEndpointTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("192.168.4.50", "http://192.168.4.50:8880")]
    [InlineData("192.168.4.50:9000", "http://192.168.4.50:9000")]
    [InlineData("http://tts-box:8880/v1", "http://tts-box:8880")]
    [InlineData("https://tts.lan/v1/audio/speech/", "https://tts.lan")]
    [InlineData("[::1]:8880", "http://[::1]:8880")]
    [InlineData("not a url ::", "")]
    public void NormalizeBaseUrl(string input, string expected) =>
        Assert.Equal(expected, KokoroEndpoint.NormalizeBaseUrl(input));

    [Fact]
    public void DescribeVoice() =>
        Assert.Equal("jf_alpha (Japanese Female)", KokoroEndpoint.DescribeVoice("jf_alpha"));
}
