using VoiceChatbot;
using Xunit;

public class PiRequestParserTests
{
    [Theory]
    [InlineData("ask pi to list the folders in this project", "list the folders in this project")]
    [InlineData("Ask Pi list the directories", "list the directories")]
    [InlineData("ask pi: what's in the src folder?", "what's in the src folder?")]
    [InlineData("Ask the Pi agent to summarize README.md", "summarize README.md")]
    [InlineData("please ask pie to look at the scripts folder", "look at the scripts folder")]
    [InlineData("Pi agent, show the folder layout", "show the folder layout")]
    [InlineData("pi agent list the directories", "list the directories")]
    [InlineData("Hey pi agent, please inspect the tests folder", "inspect the tests folder")]
    [InlineData("pie agent: check the docs directory", "check the docs directory")]
    [InlineData("Use the pi agent to look at the installer folder", "look at the installer folder")]
    [InlineData("use pi agent to display the folders", "display the folders")]
    public void ExplicitAddressIsRouted(string text, string expectedRequest)
    {
        Assert.Equal(expectedRequest, PiRequestParser.ExtractRequest(text));
        Assert.True(PiRequestParser.TryCreateReadOnlyPrompt(text, out var prompt, out var blocked));
        Assert.Equal("", blocked);
        Assert.EndsWith("User request: " + expectedRequest, prompt);
    }

    [Theory]
    [InlineData("Pie recipes for Thanksgiving?")]
    [InlineData("pie crust tips")]
    [InlineData("Pi is roughly 3.14159, right?")]
    [InlineData("pi to 50 digits please")]
    [InlineData("Use pi to calculate the area of a circle with radius 3")]
    [InlineData("use pie charts to show the sales data")]
    [InlineData("Agent Smith quotes from The Matrix")]
    [InlineData("agent please book a table")]
    [InlineData("ask agent to summarize this")]
    [InlineData("Can you look at the files I attached?")]
    [InlineData("What's in that directory listing you showed?")]
    [InlineData("Ask Pippa what she wants for dinner")]
    [InlineData("pi agent")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryChatIsNotRouted(string? text)
    {
        Assert.Equal("", PiRequestParser.ExtractRequest(text));
        Assert.False(PiRequestParser.TryCreateReadOnlyPrompt(text, out var prompt, out var blocked));
        Assert.Equal("", prompt);
        Assert.Equal("", blocked);
    }

    [Theory]
    [InlineData("ask pi to delete the bin folder")]
    [InlineData("pi agent, run the build script")]
    [InlineData("ask pi to install the npm packages")]
    public void MutatingRequestsAreBlocked(string text)
    {
        Assert.True(PiRequestParser.TryCreateReadOnlyPrompt(text, out var prompt, out var blocked));
        Assert.Equal("", prompt);
        Assert.Equal(PiRequestParser.BlockedMutationReason, blocked);
    }

    [Fact]
    public void NonInspectionRequestsGetGuidance()
    {
        Assert.True(PiRequestParser.TryCreateReadOnlyPrompt("ask pi how are you", out var prompt, out var blocked));
        Assert.Equal("", prompt);
        Assert.Equal(PiRequestParser.BlockedNotReadOnlyReason, blocked);
    }
}
