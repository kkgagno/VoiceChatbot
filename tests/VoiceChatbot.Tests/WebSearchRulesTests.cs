using VoiceChatbot;
using Xunit;

public class WebSearchRulesTests
{
    [Theory]
    [InlineData("gpt-oss-120b benchmarks")]
    [InlineData("best GPTQ quantization settings for llama.cpp")]
    [InlineData("GPT4All latest release")]
    [InlineData("latest ChatGPT features")]
    [InlineData("OpenAI news this week")]
    [InlineData("what changed in gpt-5")]
    [InlineData("migrating from node.js to deno")]
    [InlineData("upgrading from asp.net core 6 to 8")]
    [InlineData("exclude spam -site:reddit.com")]
    [InlineData("socket.io vs ws performance")]
    [InlineData("")]
    public void NoRestrictionUnlessASiteIsNamed(string query) =>
        Assert.Empty(WebSearchRules.GetExplicitDomains(query));

    [Theory]
    [InlineData("gpt-oss model card site:openai.com", "openai.com")]
    [InlineData("site:https://www.reddit.com best budget GPUs", "reddit.com")]
    [InlineData("best budget GPUs on reddit.com", "reddit.com")]
    [InlineData("best budget GPUs on www.reddit.com.", "reddit.com")]
    [InlineData("latest headlines from bbc.co.uk", "bbc.co.uk")]
    [InlineData("pricing at help.openai.com", "help.openai.com")]
    public void NamedSiteIsUsed(string query, string expected) =>
        Assert.Equal(new[] { expected }, WebSearchRules.GetExplicitDomains(query));

    [Fact]
    public void SeveralNamedSitesKeepOrderWithoutDuplicates() =>
        Assert.Equal(
            new[] { "openai.com", "help.openai.com" },
            WebSearchRules.GetExplicitDomains("site:openai.com OR site:help.openai.com gpt-oss, also on openai.com"));

    [Fact]
    public void BadKeyMessage()
    {
        var message = WebSearchRules.DescribeHttpError(401, "{\"detail\":{\"error\":\"Unauthorized: missing or invalid API key.\"}}");
        Assert.Equal("Web search failed: Tavily rejected the API key. Check the Tavily API key in Settings (HTTP 401): Unauthorized: missing or invalid API key.", message);
    }

    [Fact]
    public void QuotaMessage()
    {
        var message = WebSearchRules.DescribeHttpError(432, "{\"detail\":{\"error\":\"This request exceeds your plan's set usage limit.\"}}");
        Assert.StartsWith("Web search failed: Tavily plan usage limit reached", message);
        Assert.Contains("HTTP 432", message);
        Assert.EndsWith("This request exceeds your plan's set usage limit.", message);
    }

    [Theory]
    [InlineData(429, "{\"detail\":\"Too many requests\"}", "rate limit", "Too many requests")]
    [InlineData(433, "{\"error\":\"spending limit\"}", "pay-as-you-go", "spending limit")]
    [InlineData(502, "<html>Bad Gateway</html>", "server error", "<html>Bad Gateway</html>")]
    [InlineData(400, "{\"message\":\"query is too long\"}", "rejected the search request", "query is too long")]
    public void OtherErrors(int status, string body, string reason, string detail)
    {
        var message = WebSearchRules.DescribeHttpError(status, body);
        Assert.Contains(reason, message);
        Assert.Contains($"HTTP {status}", message);
        Assert.EndsWith(detail, message);
    }

    [Fact]
    public void EmptyBodyStillDescribesStatus() =>
        Assert.Equal("Web search failed: Tavily returned an error (HTTP 418).", WebSearchRules.DescribeHttpError(418, ""));

    [Fact]
    public void LongBodiesAreShortened()
    {
        var message = WebSearchRules.DescribeHttpError(500, new string('x', 1000));
        Assert.True(message.Length < 330);
        Assert.EndsWith("...", message);
    }
}
