using System;
using System.Linq;
using VoiceChatbot;
using Xunit;

public class SpeechPiecesTests
{
    private static string[] Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void BlankText_HasNoPieces(string? text)
    {
        Assert.Empty(SpeechPieces.Split(text));
    }

    [Fact]
    public void ShortReply_IsOnePiece()
    {
        Assert.Equal(new[] { "Sure! The meeting is at three." }, SpeechPieces.Split("  Sure! The meeting is at three.  "));
    }

    [Fact]
    public void ThreeSentenceReply_StartsWithTheFirstSentencesAndKeepsEveryWord()
    {
        const string first = "The capital of Australia is Canberra, not Sydney, which surprises a lot of people who visit the country for the first time.";
        const string second = "Canberra was chosen as a compromise between Sydney and Melbourne, the two largest cities.";
        const string third = "Parliament moved there in 1927.";
        var text = $"{first} {second} {third}";

        var pieces = SpeechPieces.Split(text);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(first, pieces[0]);
        // The short last sentence is not a clip of its own.
        Assert.Equal($"{second} {third}", pieces[1]);
        Assert.Equal(Words(text), pieces.SelectMany(Words));
    }

    [Theory]
    [InlineData(
        "Canberra is the capital of Australia, chosen as a compromise between two big cities.",
        "Parliament first met there in 1927, after years of planning and building the new city.",
        "Today it is home to about four hundred and sixty thousand people and many museums.")]
    [InlineData(
        "The capital of Australia is Canberra, which many visitors find surprising, since Sydney is far larger.",
        "It was chosen in 1908 as a compromise between Sydney and Melbourne, the two biggest and oldest cities.",
        "Parliament moved there in 1927, and today the city is known for its museums, lakes and wide green parks.")]
    public void TypicalThreeSentenceReply_StartsAfterTheFirstSentence(string first, string second, string third)
    {
        var pieces = SpeechPieces.Split($"{first} {second} {third}");

        Assert.Equal(new[] { first, $"{second} {third}" }, pieces);
    }

    [Fact]
    public void ShortLastSentence_JoinsThePieceBeforeIt()
    {
        const string first = "The meeting with the design team is on Thursday at three in the afternoon, in room B.";

        Assert.Equal(new[] { $"{first} Hope that helps!" }, SpeechPieces.Split($"{first} Hope that helps!"));
    }

    [Fact]
    public void FirstPiece_IsAtLeastTheMinimum_EvenWhenSentencesAreShort()
    {
        var text = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"Step {i} is short."));

        var pieces = SpeechPieces.Split(text);

        Assert.True(pieces[0].Length >= SpeechPieces.FirstPieceMinLength, pieces[0]);
        Assert.All(pieces, piece => Assert.True(piece.Length >= SpeechPieces.FirstPieceMinLength, piece));
        Assert.Equal(Words(text), pieces.SelectMany(Words));
    }

    [Fact]
    public void LaterPieces_GrowLonger()
    {
        var sentence = "This sentence is here to make a long answer that is spoken in several pieces.";
        var text = string.Join(" ", Enumerable.Repeat(sentence, 30));

        var pieces = SpeechPieces.Split(text);

        Assert.True(pieces.Count >= 4);
        Assert.InRange(pieces[0].Length, SpeechPieces.FirstPieceMinLength, SpeechPieces.FirstPieceMinLength + sentence.Length);
        Assert.InRange(pieces[1].Length, SpeechPieces.SecondPieceMinLength, SpeechPieces.SecondPieceMinLength + sentence.Length);
        foreach (var piece in pieces.Skip(2).SkipLast(1))
            Assert.InRange(piece.Length, SpeechPieces.LaterPieceMinLength, SpeechPieces.LaterPieceMinLength + sentence.Length);
        Assert.True(pieces[^1].Length >= SpeechPieces.FirstPieceMinLength);
        Assert.All(pieces, piece => Assert.EndsWith(".", piece));
        Assert.Equal(Words(text), pieces.SelectMany(Words));
    }

    [Fact]
    public void LinesWithoutAnEndMark_KeepTheirLineBreak()
    {
        var text = "Here is what you need for the trip, so nothing is forgotten when you leave early tomorrow morning:\n\n" +
                   "Passport\nTickets\nPhone charger";

        var pieces = SpeechPieces.Split(text);

        Assert.Single(pieces);
        Assert.Contains("tomorrow morning: Passport\nTickets\nPhone charger", pieces[0]);
    }
}

public class SpeechTimingTests
{
    [Fact]
    public void DescribesWhereTheTimeOfSpeechWent()
    {
        var timing = new SpeechTiming(TimeSpan.FromSeconds(0.84), TimeSpan.FromSeconds(2.3), TimeSpan.FromSeconds(18.5), 3,
            "Built-in Kokoro (graphics card)");

        Assert.Equal(2.3 / 18.5, timing.RealTimeFactor!.Value, 6);
        Assert.Equal("Speech timing: first audio after 0.8 s (4.1 s after your message); synthesis 2.3 s for 18.5 s of audio in 3 pieces, " +
                     "0.12 x real time; Built-in Kokoro (graphics card)", timing.Describe(TimeSpan.FromSeconds(4.08)));
        Assert.Equal("Speech timing: first audio after 0.8 s; synthesis 2.3 s for 18.5 s of audio in 3 pieces, 0.12 x real time; " +
                     "Built-in Kokoro (graphics card)", timing.Describe());
    }

    [Fact]
    public void DescribesOneClip_SpeechWhileWriting_AndStoppedSpeech()
    {
        Assert.Equal("Speech timing: first audio after 6.1 s; synthesis 6.0 s for 15.2 s of audio, 0.39 x real time; Remote Kokoro (tts-box:8880)",
            new SpeechTiming(TimeSpan.FromSeconds(6.1), TimeSpan.FromSeconds(6.0), TimeSpan.FromSeconds(15.2), 1, "Remote Kokoro (tts-box:8880)").Describe());

        Assert.Equal("Speech timing: first audio 2.5 s after the request, while the reply was written (2.6 s after your message); " +
                     "synthesis 0.4 s for 9.0 s of audio in 2 pieces, 0.04 x real time; Built-in Kokoro (graphics card)",
            new SpeechTiming(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(9), 2, "Built-in Kokoro (graphics card)",
                WhileWriting: true).Describe(TimeSpan.FromSeconds(2.6)));

        var stopped = new SpeechTiming(null, TimeSpan.FromSeconds(1.2), TimeSpan.Zero, 0, "", Stopped: true);
        Assert.Null(stopped.RealTimeFactor);
        Assert.Equal("Speech timing: nothing played; synthesis 1.2 s for 0.0 s of audio; stopped early", stopped.Describe(TimeSpan.FromSeconds(3)));
    }
}
