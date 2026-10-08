using VoiceChatbot;
using Xunit;

public class WakeWordTextTests
{
    [Theory]
    [InlineData("Hey Onyx, what's the weather?", "what's the weather?")]
    [InlineData("hey onyx what's the weather", "what's the weather")]
    [InlineData("Hey, Onyx. What time is it?", "What time is it?")]
    [InlineData("HEY ONYX! Start gemma.", "Start gemma.")]
    [InlineData("hey, onix, open the notes", "open the notes")]
    [InlineData("Hay Onyx, tell me a joke.", "tell me a joke.")]
    [InlineData("Hi Onyx. Tell me a joke.", "Tell me a joke.")]
    [InlineData("Hei Onyx tell me a joke", "tell me a joke")]
    [InlineData("A Onyx, tell me a joke.", "tell me a joke.")]
    [InlineData("Hey Annex, what's up?", "what's up?")]
    [InlineData("Hey Unix, what's up?", "what's up?")]
    [InlineData("Hey Oniks, what's up?", "what's up?")]
    [InlineData("Hey Onex what's up", "what's up")]
    [InlineData("Hey Nyx, what's up?", "what's up?")]
    [InlineData("Hey on X, what's up?", "what's up?")]
    [InlineData("Hey O Nix. What's up?", "What's up?")]
    [InlineData("Okay. Hey Onyx! Start gemma.", "Start gemma.")]
    [InlineData("Hey-Onyx, - open the notes", "open the notes")]
    public void FindsHeyOnyxAsWhisperWritesIt(string transcript, string expected)
    {
        Assert.True(WakeWordText.TryFind(transcript, WakeWordText.DefaultPhrase, out var remainder));
        Assert.Equal(expected, remainder);
    }

    [Theory]
    [InlineData("Hey Onyx.")]
    [InlineData("Hey, Onyx!")]
    [InlineData("hey onyx")]
    public void ABarePhraseLeavesNothing(string transcript)
    {
        Assert.True(WakeWordText.TryFind(transcript, "hey onyx", out var remainder));
        Assert.Equal("", remainder);
    }

    [Theory]
    [InlineData("What's the weather?")]
    [InlineData("Onyx is a black stone.")]
    [InlineData("Hey Annie, what's up?")]
    [InlineData("Hey Alex, what's up?")]
    [InlineData("Hey only one thing.")]
    [InlineData("Hey, one more thing.")]
    [InlineData("Hey, any news?")]
    [InlineData("Hey Nick, come here.")]
    [InlineData("Hey, on it.")]
    [InlineData("Hey, on next Tuesday.")]
    [InlineData("They onyx")]
    [InlineData("Hey there, Onyx.")]
    [InlineData("Hey")]
    [InlineData("")]
    public void IgnoresSpeechWithoutThePhrase(string transcript)
    {
        Assert.False(WakeWordText.TryFind(transcript, "hey onyx", out var remainder));
        Assert.Equal("", remainder);
    }

    [Fact]
    public void KeepsACustomPhrase()
    {
        Assert.True(WakeWordText.TryFind("Hey assistants, open the notes.", "hey assistant", out var remainder));
        Assert.Equal("open the notes.", remainder);
        Assert.True(WakeWordText.TryFind("Computer. Lights on.", "computer", out remainder));
        Assert.Equal("Lights on.", remainder);
        Assert.False(WakeWordText.TryFind("Hey Onyx, lights on.", "computer", out _));
    }

    [Fact]
    public void ShortWordsMustMatchExactly()
    {
        Assert.True(WakeWordText.TryFind("Hey Bob, hi.", "hey bob", out _));
        Assert.False(WakeWordText.TryFind("Hey Rob, hi.", "hey bob", out _));
    }

    [Fact]
    public void ABlankPhraseMatchesEverything()
    {
        Assert.False(WakeWordText.HasWords("  , "));
        Assert.True(WakeWordText.HasWords("hey onyx"));
        Assert.True(WakeWordText.TryFind("  What time is it?", " ", out var remainder));
        Assert.Equal("What time is it?", remainder);
    }

    [Theory]
    [InlineData("Onyx", "onyx", true)]
    [InlineData("onix", "onyx", true)]
    [InlineData("annex", "onyx", true)]
    [InlineData("onyxes", "onyx", false)]
    [InlineData("annie", "onyx", false)]
    [InlineData("only", "onyx", false)]
    [InlineData("on", "onyx", false)]
    [InlineData("", "onyx", false)]
    public void ComparesNamesWithASmallTolerance(string heard, string target, bool expected)
    {
        Assert.Equal(expected, WakeWordText.WordMatches(heard, target));
    }
}
