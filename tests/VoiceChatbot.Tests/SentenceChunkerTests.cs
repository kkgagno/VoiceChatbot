using VoiceChatbot;
using Xunit;

public class SentenceChunkerTests
{
    // Feeds text in pieces of the given size and collects every returned sentence.
    private static List<string> Stream(string text, int pieceSize, SentenceChunker? chunker = null)
    {
        chunker ??= new SentenceChunker();
        var sentences = new List<string>();
        for (var i = 0; i < text.Length; i += pieceSize)
            sentences.AddRange(chunker.Append(text.Substring(i, Math.Min(pieceSize, text.Length - i))));
        sentences.AddRange(chunker.Flush());
        return sentences;
    }

    [Fact]
    public void SplitsOnSentencePunctuation()
    {
        var sentences = SentenceChunker.Split(
            "The weather today is sunny and warm. Tomorrow will bring some rain! Do you want the weekly forecast too?");

        Assert.Equal(new[]
        {
            "The weather today is sunny and warm.",
            "Tomorrow will bring some rain!",
            "Do you want the weekly forecast too?"
        }, sentences);
    }

    [Fact]
    public void FirstSentenceIsReturnedAsSoonAsItEnds()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Append("Sure"));
        Assert.Empty(chunker.Append("!"));               // still needs to see what follows "!"
        Assert.Equal(new[] { "Sure!" }, chunker.Append(" Here"));
    }

    [Fact]
    public void DotWaitsForTheNextWordBeforeSplitting()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Append("Okay. "));          // "Okay. the..." would continue the sentence
        Assert.Equal(new[] { "Okay." }, chunker.Append("Now"));
    }

    [Fact]
    public void ShortSentencesAfterTheFirstAreMergedIntoTheNext()
    {
        var sentences = SentenceChunker.Split("Hi! Yes. No. Okay then, let me explain how this works in detail.");

        Assert.Equal(new[] { "Hi!", "Yes. No. Okay then, let me explain how this works in detail." }, sentences);
    }

    [Fact]
    public void ShortLastSentenceIsStillReturnedByFlush()
    {
        var sentences = SentenceChunker.Split("This first sentence is long enough to stand alone. Thanks!");

        Assert.Equal(new[] { "This first sentence is long enough to stand alone.", "Thanks!" }, sentences);
    }

    [Fact]
    public void WaitsForTheNextCharacterBeforeDecidingOnADot()
    {
        var chunker = new SentenceChunker();

        Assert.Empty(chunker.Append("The price went up by 3."));
        Assert.Empty(chunker.Append("5 percent this year"));
        Assert.Equal(new[] { "The price went up by 3.5 percent this year" }, chunker.Flush());
    }

    [Theory]
    [InlineData("Pi is roughly 3.14159 and e is about 2.71828 in value.")]
    [InlineData("Upgrade to version 2.4.1 before you install the new plugin.")]
    [InlineData("Open https://example.com/docs/page.html?x=1 to read the full guide.")]
    [InlineData("You can find it on example.com or at docs.python.org for details.")]
    [InlineData("Bring fruit, e.g. apples or pears, and snacks, i.e. chips.")]
    [InlineData("I met Mr. Smith and Dr. Jones at the conference yesterday.")]
    [InlineData("It was cats vs. dogs at the park, with toys, treats, etc. everywhere.")]
    [InlineData("The meeting starts at 5 p.m. tomorrow in the main hall.")]
    [InlineData("She moved to the U.S. last year to start a new job.")]
    [InlineData("The book was written by J. R. R. Tolkien a long time ago.")]
    [InlineData("It weighs approx. five kilograms when it is fully loaded.")]
    [InlineData("Well... I am not entirely sure that is going to work out.")]
    public void DoesNotSplitInsideASentence(string text)
    {
        Assert.Equal(new[] { text }, SentenceChunker.Split(text));
    }

    [Fact]
    public void SplitsOnFullWidthPunctuationWithoutSpaces()
    {
        // "It is sunny today." "That's nice!" (in quotes) "Tomorrow"
        var sentences = SentenceChunker.Split("\u4eca\u65e5\u306f\u6674\u308c\u3067\u3059\u3002\u300c\u3044\u3044\u3067\u3059\u306d\uff01\u300d\u660e\u65e5", minLength: 1);

        Assert.Equal(new[]
        {
            "\u4eca\u65e5\u306f\u6674\u308c\u3067\u3059\u3002",
            "\u300c\u3044\u3044\u3067\u3059\u306d\uff01\u300d",
            "\u660e\u65e5"
        }, sentences);
    }

    [Fact]
    public void SplitsAfterADomainThatEndsASentence()
    {
        var sentences = SentenceChunker.Split("The project lives at github.com. It has more than a thousand stars by now.");

        Assert.Equal(new[] { "The project lives at github.com.", "It has more than a thousand stars by now." }, sentences);
    }

    [Fact]
    public void SplitsAfterShortWordsThatAreNotAbbreviations()
    {
        var sentences = SentenceChunker.Split("I asked whether it was ready and they said no. Then we waited another hour.");

        Assert.Equal(new[] { "I asked whether it was ready and they said no.", "Then we waited another hour." }, sentences);
    }

    [Fact]
    public void HandlesMixedPunctuationAndWindowsLineEndings()
    {
        var sentences = SentenceChunker.Split("Can you believe it?! That really happened today.\r\n\r\nAnd then the story continued...\r\nThe end of it all came.");

        Assert.Equal(new[]
        {
            "Can you believe it?!",
            "That really happened today.",
            "And then the story continued...",
            "The end of it all came."
        }, sentences);
    }

    [Fact]
    public void SplitsAfterANumberThatEndsASentence()
    {
        var sentences = SentenceChunker.Split("The company was founded in 2024. It already has many customers today.");

        Assert.Equal(new[] { "The company was founded in 2024.", "It already has many customers today." }, sentences);
    }

    [Fact]
    public void IncludesClosingQuotesAndBrackets()
    {
        var sentences = SentenceChunker.Split(
            "He looked at me and said \"Stop right there.\" Then he walked away (very slowly.) Nobody followed.");

        Assert.Equal(new[]
        {
            "He looked at me and said \"Stop right there.\"",
            "Then he walked away (very slowly.)",
            "Nobody followed."
        }, sentences);
    }

    [Fact]
    public void IncludesClosingMarkdownEmphasis()
    {
        var sentences = SentenceChunker.Split("**This part is important.** Read the next part carefully as well.");

        Assert.Equal(new[] { "**This part is important.**", "Read the next part carefully as well." }, sentences);
    }

    [Fact]
    public void SplitsOnBlankLines()
    {
        var sentences = SentenceChunker.Split("Here is the first paragraph without a full stop\n\nAnd here is the second paragraph of text");

        Assert.Equal(new[] { "Here is the first paragraph without a full stop", "And here is the second paragraph of text" }, sentences);
    }

    [Fact]
    public void JoinsWrappedLinesOfOneSentence()
    {
        var sentences = SentenceChunker.Split("This sentence was wrapped\nonto a second line by the model.");

        Assert.Equal(new[] { "This sentence was wrapped\nonto a second line by the model." }, sentences);
    }

    [Fact]
    public void SplitsListItems()
    {
        var text = "You will need a few things:\n- A laptop with Windows installed\n- A microphone that works well\n- Some patience for the setup\n";

        var sentences = SentenceChunker.Split(text);

        Assert.Equal(new[]
        {
            "You will need a few things:",
            "- A laptop with Windows installed",
            "- A microphone that works well",
            "- Some patience for the setup"
        }, sentences);
    }

    [Fact]
    public void DoesNotTreatNumberedListMarkersAsSentenceEnds()
    {
        var text = "Follow these steps now:\n1. Open the settings window\n2. Choose the voice you like\n3. Press the save button";

        var sentences = SentenceChunker.Split(text);

        Assert.Equal(new[]
        {
            "Follow these steps now:",
            "1. Open the settings window",
            "2. Choose the voice you like",
            "3. Press the save button"
        }, sentences);
    }

    [Fact]
    public void MergesShortListItems()
    {
        var sentences = SentenceChunker.Split("Colors I like a lot:\n- Red\n- Green\n- Blue\n- Purple and orange together");

        Assert.Equal(new[] { "Colors I like a lot:", "- Red\n- Green\n- Blue\n- Purple and orange together" }, sentences);
    }

    [Fact]
    public void SplitsAfterHeadings()
    {
        var sentences = SentenceChunker.Split("## Summary of the results\nEverything worked as expected on the first try.");

        Assert.Equal(new[] { "## Summary of the results", "Everything worked as expected on the first try." }, sentences);
    }

    [Fact]
    public void SkipsFencedCodeBlocks()
    {
        var text = "Run this command first:\n```powershell\nGet-Process | Sort-Object CPU. Then stop it.\n```\nAfter that, restart the app and try again.";

        var sentences = SentenceChunker.Split(text);

        Assert.Equal(new[] { "Run this command first:", "After that, restart the app and try again." }, sentences);
    }

    [Fact]
    public void SkipsUnclosedCodeBlock()
    {
        var sentences = SentenceChunker.Split("Here is the script you asked for.\n```python\nprint('hello. world')\n");

        Assert.Equal(new[] { "Here is the script you asked for." }, sentences);
    }

    [Fact]
    public void StopAtFirstCodeBlockIgnoresEverythingAfterTheFence()
    {
        var chunker = new SentenceChunker { StopAtFirstCodeBlock = true };

        var sentences = Stream("Use this snippet:\n```js\nconsole.log(1);\n```\nIt prints one to the console window.", 3, chunker);

        Assert.Equal(new[] { "Use this snippet:" }, sentences);
        Assert.True(chunker.SawCodeBlock);
    }

    [Fact]
    public void FenceSplitAcrossPiecesIsStillDetected()
    {
        var chunker = new SentenceChunker();
        var sentences = new List<string>();

        sentences.AddRange(chunker.Append("Code below, please read it carefully. `"));
        sentences.AddRange(chunker.Append("`"));
        sentences.AddRange(chunker.Append("`bash\necho hi. there\n`"));
        sentences.AddRange(chunker.Append("``\nAll done with the explanation now."));
        sentences.AddRange(chunker.Flush());

        Assert.Equal(new[] { "Code below, please read it carefully.", "All done with the explanation now." }, sentences);
    }

    [Fact]
    public void KeepsInlineCode()
    {
        var sentences = SentenceChunker.Split("Call `Start()` before anything else happens. Then call `Stop()` when finished.");

        Assert.Equal(new[] { "Call `Start()` before anything else happens.", "Then call `Stop()` when finished." }, sentences);
    }

    [Fact]
    public void VeryLongSentenceMayEndAtAComma()
    {
        var chunker = new SentenceChunker { MaxLength = 40 };
        var text = "This sentence keeps going and going without a stop, so it is split at a comma, which keeps speech flowing";

        var sentences = Stream(text, text.Length, chunker);

        Assert.Equal(new[]
        {
            "This sentence keeps going and going without a stop,",
            "so it is split at a comma, which keeps speech flowing"
        }, sentences);
    }

    [Fact]
    public void IgnoresTextWithoutLettersOrDigits()
    {
        Assert.Empty(SentenceChunker.Split("---\n\n***\n"));
        Assert.Empty(SentenceChunker.Split(""));
        Assert.Empty(SentenceChunker.Split(null));
    }

    [Fact]
    public void FlushResetsTheChunker()
    {
        var chunker = new SentenceChunker();
        chunker.Append("First reply that is not finished");
        Assert.Equal(new[] { "First reply that is not finished" }, chunker.Flush());

        Assert.Equal(new[] { "Second!" }, chunker.Append("Second! "));
        Assert.Empty(chunker.Flush());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public void StreamingInAnyPieceSizeGivesTheSameResult(int pieceSize)
    {
        var text =
            "Sure! Here is a quick overview of version 3.2.1, released by Dr. Smith's team at example.org.\n\n" +
            "## What changed\n" +
            "- Startup is about 2.5x faster than before.\n" +
            "- The settings page moved, e.g. to the sidebar.\n" +
            "1. Install it.\n2. Restart.\n\n" +
            "```bash\nnpm install thing. now\n```\n" +
            "That's it... mostly. Questions? Ask away!\n" +
            "He said \"Done.\" Then he left the room for the day.";

        var expected = SentenceChunker.Split(text);

        Assert.Equal(expected, Stream(text, pieceSize));
        Assert.DoesNotContain(expected, s => s.Contains("npm"));
        Assert.Equal("Sure!", expected[0]);
    }

    // ---- EveryPieceAtLeastMinLength (live speech: no short clips) ----

    private static SentenceChunker LongPieces(int minLength = 120) =>
        new() { MinLength = minLength, EveryPieceAtLeastMinLength = true, StopAtFirstCodeBlock = true };

    private const string LongReply =
        "Sure! Here is a quick plan for your weekend trip to the mountains, starting on Saturday morning. " +
        "Pack warm layers, because it gets cold at night. Bring a map too. " +
        "On Sunday you can hike the lake trail, which takes about three hours and has great views at the top. " +
        "Enjoy!";

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1000)]
    public void EveryPieceIsAtLeastTheMinimumLength(int pieceSize)
    {
        var pieces = Stream(LongReply, pieceSize, LongPieces());

        Assert.True(pieces.Count >= 2);
        Assert.All(pieces, p => Assert.True(p.Length >= 120, $"Too short: \"{p}\""));
        Assert.Equal(Normalize(LongReply), Normalize(string.Join(" ", pieces)));
    }

    [Fact]
    public void FirstSentenceIsMergedWithTheNextOnes()
    {
        var pieces = Stream(LongReply, 3, LongPieces());

        Assert.StartsWith("Sure! Here is a quick plan", pieces[0]);
        Assert.DoesNotContain("Sure!", pieces);
    }

    [Fact]
    public void ShortLastSentenceIsJoinedToThePieceBeforeIt()
    {
        var pieces = Stream(LongReply, 4, LongPieces());

        Assert.EndsWith("great views at the top. Enjoy!", pieces[^1]);
        Assert.DoesNotContain("Enjoy!", pieces);
    }

    [Fact]
    public void APieceWaitsUntilEnoughTextFollowsIt()
    {
        var chunker = LongPieces(minLength: 40);
        var first = "This opening sentence is long enough to stand. ";

        Assert.Empty(chunker.Append(first));
        Assert.Empty(chunker.Append("Short one. "));     // only 11 characters follow the first piece
        Assert.Equal(new[] { first.Trim() }, chunker.Append("And now enough text follows it here."));
        Assert.Equal(new[] { "Short one. And now enough text follows it here." }, chunker.Flush());
    }

    [Fact]
    public void AShortReplyIsOnePiece()
    {
        var pieces = Stream("Hi! What can I help you with?", 2, LongPieces());

        Assert.Equal(new[] { "Hi! What can I help you with?" }, pieces);
    }

    [Fact]
    public void PreambleOfAToolRoundIsSpokenWithTheAnswer()
    {
        var chunker = LongPieces();
        var pieces = new List<string>(chunker.Append("Let me look that up."));
        pieces.AddRange(chunker.Append("\n\n"));       // the tool round ends; the answer streams next
        pieces.AddRange(chunker.Append(LongReply));
        pieces.AddRange(chunker.Flush());

        Assert.StartsWith("Let me look that up.\n\nSure!", pieces[0]);
        Assert.All(pieces, p => Assert.True(p.Length >= 120));
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public class SpeechMarkdownTests
{
    [Theory]
    [InlineData("**Bold** and *italic* and __under__ text.", "Bold and italic and under text.")]
    [InlineData("## Heading here", "Heading here")]
    [InlineData("- A bullet item", "A bullet item")]
    [InlineData("3. A numbered item", "A numbered item")]
    [InlineData("> A quoted line", "A quoted line")]
    [InlineData("See [the docs](https://example.com) for more.", "See the docs for more.")]
    [InlineData("Call `Start()` first.", "Call Start() first.")]
    [InlineData("Line <br> break", "Line break")]
    [InlineData("**Speed:** It is fast.", "Speed: It is fast.")]
    [InlineData("**Half bold sentence.", "Half bold sentence.")]
    [InlineData("Great job \U0001F600!", "Great job !")]
    [InlineData("", "")]
    public void StripsMarkdown(string input, string expected) =>
        Assert.Equal(expected, SpeechMarkdown.ToPlainText(input));
}
