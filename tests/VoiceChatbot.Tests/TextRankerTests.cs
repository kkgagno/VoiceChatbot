using VoiceChatbot;
using Xunit;

public class TextRankerTests
{
    [Fact]
    public void Tokenize_LowercasesStripsPunctuationAndStopwords() =>
        Assert.Equal(
            new[] { "dog", "bark", "loud", "night" },
            TextRanker.Tokenize("The DOG's barking LOUD, at night!"));

    [Fact]
    public void Tokenize_HandlesBlankAndApostrophes()
    {
        Assert.Empty(TextRanker.Tokenize(null));
        Assert.Empty(TextRanker.Tokenize("   "));
        Assert.Empty(TextRanker.Tokenize("Don't you’re about it?"));
        Assert.Equal(new[] { "rtx4090", "gpu" }, TextRanker.Tokenize("RTX4090 GPUs"));
    }

    [Theory]
    [InlineData("batteries", "battery")]
    [InlineData("coding", "code")]
    [InlineData("codes", "coded")]
    [InlineData("running", "runs")]
    [InlineData("stopped", "stop")]
    [InlineData("played", "playing")]
    [InlineData("carried", "carry")]
    [InlineData("studies", "studying")]
    [InlineData("boxes", "box")]
    [InlineData("classes", "class")]
    [InlineData("embedded", "embedding")]
    [InlineData("making", "make")]
    public void Stem_MapsVariantsTogether(string a, string b) =>
        Assert.Equal(TextRanker.Stem(a), TextRanker.Stem(b));

    [Theory]
    [InlineData("status")]
    [InlineData("analysis")]
    [InlineData("add")]
    [InlineData("fall")]
    [InlineData("dog")]
    public void Stem_LeavesWordsThatOnlyLookSuffixed(string word) =>
        Assert.Equal(word, TextRanker.Stem(word));

    [Fact]
    public void Rank_PutsBestMatchFirstAndSkipsNonMatches()
    {
        var ranker = new TextRanker(new[]
        {
            "We talked about the user's dog Rex and his vet visit.",
            "Set up Ollama with a Gemma model on the Ryzen laptop.",
            "Planned a birthday party for the dogs at the park.",
            "Discussed tax forms."
        });

        var results = ranker.Rank("How is my dog Rex doing?");

        Assert.Equal(new[] { 0, 2 }, results.Select(r => r.Index));
        Assert.True(results[0].Score > results[1].Score);
        Assert.All(results, r => Assert.True(r.Score > 0));
    }

    [Fact]
    public void Rank_RareTermsOutweighCommonOnes()
    {
        var ranker = new TextRanker(new[]
        {
            "model server setup",
            "model server kubernetes",
            "model server logs",
        });

        Assert.Equal(1, ranker.Rank("model kubernetes")[0].Index);
    }

    [Fact]
    public void Rank_ShorterDocumentWinsForSameMatch()
    {
        var ranker = new TextRanker(new[]
        {
            "pizza recipe with lots of extra words about ovens flour cheese tomatoes basil",
            "pizza recipe",
        });

        Assert.Equal(1, ranker.Rank("pizza")[0].Index);
    }

    [Fact]
    public void Rank_TopLimitsAndTiesKeepInputOrder()
    {
        var ranker = new TextRanker(new[] { "cats", "cats", "cats", "dogs" });

        var results = ranker.Rank("cat", top: 2);

        Assert.Equal(new[] { 0, 1 }, results.Select(r => r.Index));
    }

    [Fact]
    public void Rank_EmptyInputsReturnNothing()
    {
        Assert.Empty(new TextRanker(Array.Empty<string>()).Rank("anything"));
        Assert.Empty(new TextRanker(new[] { "hello world" }).Rank(""));
        Assert.Empty(new TextRanker(new[] { "hello world" }).Rank("the and of"));
        Assert.Empty(new TextRanker(new string?[] { null, "" }).Rank("hello"));
    }

    [Fact]
    public void Score_MatchesRankAndRejectsBadIndex()
    {
        var ranker = new TextRanker(new[] { "solar panels", "wind turbines" });

        Assert.Equal(0, ranker.Score("solar", 1));
        Assert.Equal(ranker.Rank("solar")[0].Score, ranker.Score("solar", 0), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => ranker.Score("solar", 2));
    }

    [Fact]
    public void Rank_RepeatedQueryWordsDoNotChangeOrder()
    {
        var ranker = new TextRanker(new[] { "garden tomatoes", "garden roses and tulips" });

        Assert.Equal(
            ranker.Rank("garden roses").Select(r => r.Index),
            ranker.Rank("garden garden garden roses").Select(r => r.Index));
    }
}
