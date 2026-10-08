using VoiceChatbot;
using Xunit;

public class TranscriptCleanupTests
{
    [Fact]
    public void JoinsSegmentsWithASpace()
    {
        Assert.Equal("Hello there. How are you?", TranscriptCleanup.JoinSegments(new[] { "Hello there.", " How are you?" }));
        Assert.Equal("one two", TranscriptCleanup.JoinSegments(new[] { "one", "two" }));
    }

    [Fact]
    public void NoSpaceBeforePunctuationThatContinuesTheSentence()
    {
        Assert.Equal("Well, then.", TranscriptCleanup.JoinSegments(new[] { "Well", ", then." }));
        Assert.Equal("Yes.", TranscriptCleanup.JoinSegments(new[] { "Yes", "." }));
    }

    [Fact]
    public void SkipsBlankSegments()
    {
        Assert.Equal("a b", TranscriptCleanup.JoinSegments(new[] { "", "a", null, "  ", "b" }));
        Assert.Equal("", TranscriptCleanup.JoinSegments(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("What the fuck is that?", "What the fuck is that?")]
    [InlineData("This is bullshit.", " this is bullshit")]
    [InlineData("I'm here.", "Im here!")]
    public void DropsASegmentThatRepeatsThePreviousOne(string first, string second)
    {
        Assert.Equal(first, TranscriptCleanup.JoinSegments(new[] { first, second }));
    }

    [Fact]
    public void KeepsDifferentSegmentsAndNonAdjacentRepeatsForLater()
    {
        Assert.Equal("Turn it on. Turn it off.", TranscriptCleanup.JoinSegments(new[] { "Turn it on.", "Turn it off." }));
        Assert.Equal("A. B. A.", TranscriptCleanup.JoinSegments(new[] { "A.", "B.", "A." }));
    }

    [Theory]
    [InlineData("What the fuck is that?What the fuck is that?", "What the fuck is that?")]
    [InlineData("This is bullshit.This is bullshit.", "This is bullshit.")]
    [InlineData("I'm going to do the same thing. I'm going to do the same thing. I'm going to do the same thing. I'm going to do the same thing.",
        "I'm going to do the same thing.")]
    [InlineData("X. X. X.", "X.")]
    [InlineData("Hello. hello", "Hello.")]
    [InlineData("A. B. A. B.", "A. B.")]
    [InlineData("Okay. What time is it? What time is it?", "Okay. What time is it?")]
    public void CollapsesBackToBackRepeats(string input, string expected)
    {
        Assert.Equal(expected, TranscriptCleanup.CollapseRepeatedSentences(input));
    }

    [Theory]
    [InlineData("What time is it? It's late.")]
    [InlineData("No, no, no.")]
    [InlineData("A. B. A.")]
    [InlineData("Version 3.3 is out.")]
    [InlineData("Set it to 3.3. Then 3.4.")]
    [InlineData("Turn it on. Turn it off. Turn it on.")]
    [InlineData("Hello")]
    [InlineData("")]
    public void LeavesTextWithoutRepeatsUnchanged(string input)
    {
        Assert.Equal(input, TranscriptCleanup.CollapseRepeatedSentences(input));
    }

    [Fact]
    public void CleanJoinsDropsAndCollapses()
    {
        Assert.Equal("What the fuck is that?",
            TranscriptCleanup.Clean(new[] { "What the fuck is that?", "What the fuck is that?" }));
        Assert.Equal("I'm going to do the same thing.",
            TranscriptCleanup.Clean(new[] { "I'm going to do the same thing. I'm going to do the same thing.", "I'm going to do the same thing." }));
        Assert.Equal("Open the notes. Then read them.",
            TranscriptCleanup.Clean(new[] { "Open the notes.", "Then read them." }));
    }
}
