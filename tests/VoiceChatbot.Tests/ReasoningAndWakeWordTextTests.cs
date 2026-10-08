using VoiceChatbot;
using Xunit;

public class ReasoningAndWakeWordTextTests
{
    [Theory]
    [InlineData("<think>The user wants a greeting.</think>\n\nHello there!", "Hello there!")]
    [InlineData("<THINK>\nplan\n</THINK>Hi", "Hi")]
    [InlineData("Answer one. <think>more</think>Answer two.", "Answer one. Answer two.")]
    [InlineData("<think>still reasoning about it", "")]
    [InlineData("  <think>\nstill reasoning", "")]
    [InlineData("Plain answer without tags.", "Plain answer without tags.")]
    [InlineData("Use <b>bold</b> here.", "Use <b>bold</b> here.")]
    public void StripsThinkBlocks(string input, string expected)
    {
        Assert.Equal(expected, ReasoningText.StripThinking(input));
    }

    [Fact]
    public void StreamingHoldsBackAPartialLeadingTag()
    {
        Assert.Equal("", ReasoningText.StripThinking("<th", streaming: true));
        Assert.Equal("<th", ReasoningText.StripThinking("<th"));
        Assert.Equal("<b>Hi", ReasoningText.StripThinking("<b>Hi", streaming: true));
    }

    [Fact]
    public void StreamedTextOnlyGrowsOnceTheBlockCloses()
    {
        var raw = "";
        var shown = new List<string>();
        foreach (var token in new[] { "<", "think", ">", "plan", "</th", "ink>", "\n\n", "Hello", " world." })
        {
            raw += token;
            shown.Add(ReasoningText.StripThinking(raw, streaming: true));
        }

        Assert.All(shown.Take(7), s => Assert.Equal("", s));
        Assert.Equal("Hello", shown[7]);
        Assert.Equal("Hello world.", shown[8]);
    }

    [Theory]
    [InlineData("Hey, assistant. What time is it?", "What time is it?")]
    [InlineData("hey assistant what's the weather", "what's the weather")]
    [InlineData("Okay. Hey Assistant! Start gemma.", "Start gemma.")]
    [InlineData("Hey assistant.", "")]
    [InlineData("Hey-assistant, - open the notes", "open the notes")]
    public void FindsWakeWordIgnoringPunctuationAndCase(string transcript, string expected)
    {
        Assert.True(WakeWordText.TryFind(transcript, "hey assistant", out var remainder));
        Assert.Equal(expected, remainder);
    }

    [Theory]
    [InlineData("Hey there, assistant.")]
    [InlineData("They assistant")]
    [InlineData("")]
    public void RejectsTranscriptsWithoutTheWakeWord(string transcript)
    {
        Assert.False(WakeWordText.TryFind(transcript, "hey assistant", out _));
    }
}
