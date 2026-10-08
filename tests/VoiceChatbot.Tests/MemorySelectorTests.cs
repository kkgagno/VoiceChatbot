using VoiceChatbot;
using Xunit;

public class MemorySelectorTests
{
    // Oldest first, like MemoryManager.LoadAll().
    private static readonly string[] Memories =
    {
        "User's dog Rex had a vet visit about his limp.",          // 0
        "Set up Ollama with Gemma on the Ryzen laptop.",           // 1
        "Planned a vegetable garden with tomatoes and peppers.",   // 2
        "Talked about Rex learning to fetch at the park.",         // 3
        "Fixed the Kokoro TTS server port to 8880.",               // 4
        "Discussed weekend hiking plans in the mountains.",        // 5
    };

    [Fact]
    public void Relevant_ReturnsMatchesPlusNewestInOrder() =>
        Assert.Equal(new[] { 0, 3, 5 }, MemorySelector.SelectIndices(Memories, "How is Rex's limp?", "Relevant", 4));

    [Fact]
    public void Relevant_RespectsMaxItems() =>
        // One best match ("Rex" and "park") plus the newest memory.
        Assert.Equal(new[] { 3, 5 }, MemorySelector.SelectIndices(Memories, "Rex at the park", "Relevant", 1));

    [Fact]
    public void Relevant_NoMatchesKeepsOnlyNewest() =>
        Assert.Equal(new[] { 5 }, MemorySelector.SelectIndices(Memories, "Quantum chromodynamics lecture", "Relevant", 4));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok then")]
    public void Relevant_WithoutUsableTextFallsBackToMostRecent(string? text) =>
        Assert.Equal(new[] { 2, 3, 4, 5 }, MemorySelector.SelectIndices(Memories, text, "Relevant", 4));

    [Fact]
    public void Relevant_RecallQuestionPadsWithRecentMemories() =>
        Assert.Equal(new[] { 2, 3, 4, 5 }, MemorySelector.SelectIndices(Memories, "What do you remember?", "Relevant", 4));

    [Fact]
    public void Relevant_RecallQuestionKeepsTopicMatches() =>
        // The Ollama match first, then the most recent memories up to the limit of 3.
        Assert.Equal(new[] { 1, 4, 5 }, MemorySelector.SelectIndices(Memories, "Do you remember the Ollama Gemma setup?", "Relevant", 3));

    [Fact]
    public void All_ReturnsEverything() =>
        Assert.Equal(Enumerable.Range(0, Memories.Length), MemorySelector.SelectIndices(Memories, "Rex", "All", 1));

    [Fact]
    public void FewerMemoriesThanLimit()
    {
        Assert.Empty(MemorySelector.SelectIndices(Array.Empty<string>(), "Rex", "Relevant", 4));
        Assert.Equal(new[] { 0 }, MemorySelector.SelectIndices(new[] { "only one" }, null, "Relevant", 4));
        Assert.Equal(new[] { 0, 1 }, MemorySelector.SelectIndices(new[] { "a dog", "a cat" }, "dog", "Relevant", 4));
    }

    [Theory]
    [InlineData(null, "Relevant")]
    [InlineData("all", "All")]
    [InlineData(" Relevant ", "Relevant")]
    [InlineData("something else", "Relevant")]
    public void NormalizeMode(string? input, string expected) =>
        Assert.Equal(expected, MemorySelector.NormalizeMode(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(500, 20)]
    public void ClampMaxItems(int input, int expected) =>
        Assert.Equal(expected, MemorySelector.ClampMaxItems(input));

    [Theory]
    [InlineData("What do you remember about me?", true)]
    [InlineData("What did we talk about last time?", true)]
    [InlineData("Summarize our previous conversations", true)]
    [InlineData("How do I set up a router?", false)]
    [InlineData(null, false)]
    public void IsRecallQuery(string? text, bool expected) =>
        Assert.Equal(expected, MemorySelector.IsRecallQuery(text));

    [Fact]
    public void Select_ReturnsItemsOldestFirst()
    {
        var items = Memories.Select((m, i) => (Id: i, Text: m)).ToList();

        var picked = MemorySelector.Select(items, x => x.Text, "Kokoro port", "Relevant", 4);

        Assert.Equal(new[] { 4, 5 }, picked.Select(x => x.Id));
    }
}
