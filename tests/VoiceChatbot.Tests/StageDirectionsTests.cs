using VoiceChatbot;
using Xunit;

public class StageDirectionsTests
{
    [Theory]
    [InlineData("(The AI responds with a warm, reassuring tone.)\n\nI am right here!", "I am right here!")]
    [InlineData("*smiles warmly*\nHello there.", "Hello there.")]
    [InlineData("[pauses thoughtfully]\nGood question.", "Good question.")]
    [InlineData("(chuckles) That is a fun one.", "That is a fun one.")]
    [InlineData("Sure.\n\n(The assistant nods.)\n\nAnything else?", "Sure.\n\nAnything else?")]
    public void RemovesNarration(string input, string expected) =>
        Assert.Equal(expected, StageDirections.Strip(input));

    [Theory]
    [InlineData("The speed of light (in a vacuum) is constant.")]
    [InlineData("(Optional) Restart the server after updating.")]
    [InlineData("**Note:** keep the backup.")]
    [InlineData("```\n(the ai responds)\n```")]
    [InlineData("Use arr[0] here.")]
    public void KeepsOrdinaryText(string input) =>
        Assert.Equal(input, StageDirections.Strip(input));
}
