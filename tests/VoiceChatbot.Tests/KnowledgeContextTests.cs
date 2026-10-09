using VoiceChatbot;
using Xunit;

public class KnowledgeContextTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vc-knowledge-context-tests", "townhouse");

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static KnowledgeFileRecord Record(string path, params string[] chunks) => new()
    {
        Path = path,
        Size = 100,
        LastWriteUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        Chunks = chunks.ToList()
    };

    // A small townhouse folder: fits any reasonable budget whole.
    private static KnowledgeIndex SmallIndex()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("HOA", "Bylaws.md"),
            "Article 4. Pets. Each unit may keep two pets. Dogs must be leashed in the common areas.",
            "Article 7. Parking. Each unit has two assigned spaces. Guests park in the visitor lot."));
        index.Upsert(Record(P("Insurance policy.md"),
            "The policy covers the interior walls, fixtures and personal property. Deductible: 1,000 dollars."));
        index.Upsert(Record(P("Closing", "Inspection report.md"),
            "The roof is in fair condition. The water heater was installed in 2015 and should be replaced soon."));
        index.Upsert(new KnowledgeFileRecord { Path = P("Scans", "Deed.pdf"), Error = "OCR unavailable: could not find pdftoppm.exe from Poppler." });
        return index;
    }

    // A folder that is too big to send whole: 30 files of 6 long chunks each.
    private static KnowledgeIndex LargeIndex()
    {
        var index = new KnowledgeIndex(Root);
        for (var f = 0; f < 30; f++)
        {
            var chunks = Enumerable.Range(0, 6)
                .Select(c => $"Minutes of board meeting {f}, part {c}. " + string.Join(" ", Enumerable.Repeat($"Routine business item {f}-{c} was discussed and approved.", 12)))
                .ToArray();
            index.Upsert(Record(P("Minutes", $"Meeting {f:00}.md"), chunks));
        }

        index.Upsert(Record(P("HOA", "Pool rules.md"),
            "Pool hours are 9am to 8pm. Children under 12 need an adult. No glass near the pool."));
        return index;
    }

    // ==================== Budget ====================

    [Fact]
    public void BudgetTokens_IsAThirdOfTheWindowButLeavesRoomForTheChat()
    {
        // 35% of 16k (5,734) is less than 60% of the room left (16,384 - 2,048 - 4,096 - 100).
        Assert.Equal(5734, KnowledgeContext.BudgetTokens(16384, 2048, 100));
        // Small window: 60% of the prompt room (8,192 - 2,048 - 2,048 = 4,096) wins over 35% (2,867).
        Assert.Equal(2457, KnowledgeContext.BudgetTokens(8192, 2048, 0));
        // A long message leaves less room.
        Assert.True(KnowledgeContext.BudgetTokens(16384, 2048, 9000) < KnowledgeContext.BudgetTokens(16384, 2048, 100));
        Assert.Equal(0, KnowledgeContext.BudgetTokens(16384, 2048, 20000));
        // Unknown window: plan with the default.
        Assert.Equal(KnowledgeContext.BudgetTokens(TokenBudget.DefaultContextWindow, 2048, 0), KnowledgeContext.BudgetTokens(0, 2048, 0));
    }

    [Fact]
    public void EstimateTokens_UsesFourCharactersPerToken()
    {
        Assert.Equal(0, KnowledgeContext.EstimateTokens(null));
        Assert.Equal(1, KnowledgeContext.EstimateTokens("abc"));
        Assert.Equal(3, KnowledgeContext.EstimateTokens(new string('x', 12)));
    }

    // ==================== Catalog ====================

    [Fact]
    public void FormatCatalog_NamesTheFolderAndItsFilesWithAnInstruction()
    {
        var files = new[] { Path.Combine("HOA", "Bylaws.md"), "Insurance policy.md" };

        var catalog = KnowledgeContext.FormatCatalog("townhouse", files, new[] { "Deed.pdf" }, KnowledgeContextMode.Excerpts);

        Assert.StartsWith(KnowledgeContext.ContextHeading, catalog);
        Assert.Contains("These are the owner's documents in the knowledge folder 'townhouse' (2 files).", catalog);
        Assert.Contains("Use the excerpts below when relevant; if they do not contain the answer, say which documents might, and do not invent contents.", catalog);
        Assert.Contains("- " + Path.Combine("HOA", "Bylaws.md"), catalog);
        Assert.Contains("- Insurance policy.md", catalog);
        Assert.Contains("no text could be read from: Deed.pdf", catalog);
    }

    [Theory]
    [InlineData(KnowledgeContextMode.WholeFolder)]
    [InlineData(KnowledgeContextMode.Excerpts)]
    [InlineData(KnowledgeContextMode.Overview)]
    [InlineData(KnowledgeContextMode.Catalog)]
    public void FormatCatalog_AsksToQuoteTheLineANumberCameFrom(KnowledgeContextMode mode)
    {
        var catalog = KnowledgeContext.FormatCatalog("taxes", new[] { "1040 2022.pdf" }, null, mode);

        Assert.Contains(KnowledgeContext.QuoteTheSource, catalog);
        Assert.Contains("quote the exact line it came from and name the file", catalog);
        Assert.Contains("say it is unclear instead of guessing", catalog);
        Assert.Contains("re-read the text and quote it rather than offering another guess", catalog);
        // The column mark the PDF reader uses is explained.
        Assert.Contains($"\"{PdfLayoutText.ColumnSeparator}\" between columns", catalog);
        Assert.EndsWith(KnowledgeContext.IgnoreWhenUnrelated, catalog.Split('\n')[1].TrimEnd());
    }

    [Fact]
    public void Build_AskingAboutAFormAmountGetsTheQuotingRule()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(new KnowledgeFileRecord
        {
            Path = Path.Combine(Root, "1040 2022.pdf"),
            Chunks = KnowledgeIndex.ChunkText("Form 1040 U.S. Individual Income Tax Return 2022\n" +
                                               "9 Add lines 1z through 8. This is your total income ... 9 | 114,423\n" +
                                               "11 Subtract line 10 from line 9. This is your adjusted gross income ... 11 | 112,258")
        });

        var result = KnowledgeContext.Build(index, "What was my adjusted gross income in 2022?", null, 4, 5000);

        Assert.NotEqual(KnowledgeContextMode.None, result.Mode);
        Assert.Contains(KnowledgeContext.QuoteTheSource, result.Text);
        Assert.Contains("adjusted gross income ... 11 | 112,258", result.Text);
    }

    [Fact]
    public void FormatCatalog_CapsTheListAndCountsTheRest()
    {
        var files = Enumerable.Range(1, 75).Select(i => $"Doc {i:00}.md").ToList();

        var catalog = KnowledgeContext.FormatCatalog("townhouse", files, null, KnowledgeContextMode.Catalog);

        Assert.Contains("(75 files)", catalog);
        Assert.Contains("- Doc 60.md", catalog);
        Assert.DoesNotContain("Doc 61.md", catalog);
        Assert.Contains("...and 15 more", catalog);
        Assert.Contains("only the file names are listed", catalog);
    }

    [Fact]
    public void FormatCatalog_ForAFolderWithoutReadableText()
    {
        var catalog = KnowledgeContext.FormatCatalog("townhouse", Array.Empty<string>(), new[] { "Deed.pdf", "Bylaws scan.pdf" }, KnowledgeContextMode.Catalog);

        Assert.Contains("no text could be read from any of them", catalog);
        Assert.Contains("Unreadable: Deed.pdf, Bylaws scan.pdf.", catalog);
        Assert.Contains("do not invent contents", catalog);
        // Nothing to quote from.
        Assert.DoesNotContain(KnowledgeContext.QuoteTheSource, catalog);
    }

    // ==================== Build ====================

    [Fact]
    public void Build_SmallTalkGetsNothing()
    {
        // Even a bare file list makes the model steer the chat towards the documents.
        Assert.Equal(KnowledgeContextResult.None, KnowledgeContext.Build(SmallIndex(), "Tell me a joke", null, 4, 5000));
        Assert.Equal(KnowledgeContextResult.None, KnowledgeContext.Build(SmallIndex(), "What's the weather like tomorrow?", null, 4, 5000));
    }

    [Fact]
    public void Build_AQuestionNamingAFileGetsTheDocuments()
    {
        var result = KnowledgeContext.Build(SmallIndex(), "Who is my insurance with?", null, 4, 5000);

        Assert.NotEqual(KnowledgeContextMode.None, result.Mode);
        Assert.Contains("Insurance policy.md", result.Text);
        Assert.Contains(KnowledgeContext.IgnoreWhenUnrelated, result.Text);
    }

    [Theory]
    [InlineData("What does my deed say?", true)]
    [InlineData("anything about the inspection?", true)]
    [InlineData("can you scan this for me", false)]
    [InlineData("tell me a joke", false)]
    [InlineData("what happened in 2015", false)]
    public void MentionsFileNames_UsesTellingWordsOnly(string message, bool expected)
    {
        var names = new[] { Path.Combine("Scans", "Deed scan final.pdf"), Path.Combine("Closing", "Inspection report 2015.md") };

        Assert.Equal(expected, KnowledgeContext.MentionsFileNames(message, names));
    }

    [Fact]
    public void Build_SendsASmallFolderWholeForAQuestionAboutIt()
    {
        var result = KnowledgeContext.Build(SmallIndex(), "How many pets can I keep?", null, 1, 5000);

        Assert.Equal(KnowledgeContextMode.WholeFolder, result.Mode);
        Assert.Equal(3, result.DocumentsUsed);
        Assert.Equal("Using all 3 documents in 'townhouse'", result.Note);
        // Every chunk, file by file in folder order.
        Assert.Contains("leashed", result.Text);
        Assert.Contains("visitor lot", result.Text);
        Assert.Contains("water heater", result.Text);
        Assert.Contains("Deductible", result.Text);
        var closing = result.Text.IndexOf("[1] " + Path.Combine("Closing", "Inspection report.md"), StringComparison.Ordinal);
        var hoa = result.Text.IndexOf("[2] " + Path.Combine("HOA", "Bylaws.md"), StringComparison.Ordinal);
        var insurance = result.Text.IndexOf("[3] Insurance policy.md", StringComparison.Ordinal);
        Assert.True(closing > 0 && closing < hoa && hoa < insurance);
        Assert.True(result.Text.IndexOf("Article 4", StringComparison.Ordinal) < result.Text.IndexOf("Article 7", StringComparison.Ordinal));
        Assert.True(KnowledgeContext.EstimateTokens(result.Text) <= 5000);
    }

    [Fact]
    public void Build_AMessageAboutTheDocumentsGetsThemEvenWithoutMatchingWords()
    {
        Assert.Equal(KnowledgeContextMode.WholeFolder, KnowledgeContext.Build(SmallIndex(), "Summarize my documents", null, 4, 5000).Mode);
        Assert.Equal(KnowledgeContextMode.WholeFolder, KnowledgeContext.Build(SmallIndex(), "What's in the townhouse folder?", null, 4, 5000).Mode);
    }

    [Fact]
    public void Build_UsesRankedExcerptsWhenTheFolderDoesNotFit()
    {
        var index = LargeIndex();

        var result = KnowledgeContext.Build(index, "What are the pool hours?", null, 4, 1500);

        Assert.Equal(KnowledgeContextMode.Excerpts, result.Mode);
        Assert.NotEmpty(result.Excerpts);
        Assert.Equal(P("HOA", "Pool rules.md"), result.Excerpts[0].Path);
        Assert.Contains("9am to 8pm", result.Text);
        Assert.StartsWith("Using 1 excerpt from: Pool rules.md", result.Note);
        Assert.True(KnowledgeContext.EstimateTokens(result.Text) <= 1500);
        // The catalog still lists the folder.
        Assert.Contains("(31 files)", result.Text);
    }

    [Fact]
    public void Build_AlwaysStaysWithinTheBudget()
    {
        var index = LargeIndex();
        foreach (var budget in new[] { 200, 400, 800, 1500, 3000, 6000, 12000 })
        {
            foreach (var query in new[] { "routine business item discussed", "pool hours", "Summarize my documents", "hello there" })
            {
                var result = KnowledgeContext.Build(index, query, null, 10, budget);
                Assert.True(KnowledgeContext.EstimateTokens(result.Text) <= budget,
                    $"budget {budget}, query '{query}': {KnowledgeContext.EstimateTokens(result.Text)} tokens ({result.Mode})");
            }
        }

        Assert.Equal(KnowledgeContextMode.None, KnowledgeContext.Build(index, "pool hours", null, 4, 0).Mode);
    }

    [Fact]
    public void Build_AddsMoreThanTheMinimumWhenTheyMatchAndFit()
    {
        var index = LargeIndex();

        var roomy = KnowledgeContext.Build(index, "routine business discussed approved", null, 1, 6000);
        var tight = KnowledgeContext.Build(index, "routine business discussed approved", null, 1, 700);

        Assert.Equal(KnowledgeContextMode.Excerpts, roomy.Mode);
        Assert.True(roomy.Excerpts.Count > 1);
        Assert.True(tight.Excerpts.Count < roomy.Excerpts.Count);
        Assert.True(KnowledgeContext.EstimateTokens(roomy.Text) <= 6000);
    }

    [Fact]
    public void Build_ShowsTheStartOfEachDocumentWhenAskedAboutABigFolder()
    {
        var result = KnowledgeContext.Build(LargeIndex(), "What's in my documents?", null, 4, 3000);

        Assert.Equal(KnowledgeContextMode.Overview, result.Mode);
        Assert.Contains("The beginning of each document:", result.Text);
        Assert.Contains("part 0", result.Text);
        Assert.DoesNotContain("part 1.", result.Text);
        Assert.StartsWith("Using the beginning of ", result.Note);
        Assert.True(KnowledgeContext.EstimateTokens(result.Text) <= 3000);
    }

    [Fact]
    public void Build_TellsTheModelAboutAFolderOfUnreadableScans()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(new KnowledgeFileRecord { Path = P("Deed.pdf"), Error = "OCR unavailable" });

        var result = KnowledgeContext.Build(index, "What does my deed say?", null, 4, 5000);

        Assert.Equal(KnowledgeContextMode.Catalog, result.Mode);
        Assert.Contains("Deed.pdf", result.Text);
        Assert.Contains("could not be read", result.Text);
        Assert.Equal(KnowledgeContextResult.None, KnowledgeContext.Build(new KnowledgeIndex(Root), "deed", null, 4, 5000));
    }

    [Fact]
    public void Build_ShortFollowUpUsesThePreviousQuestion()
    {
        // "what about it?" has no words of its own; the previous question finds the pool rules.
        var result = KnowledgeContext.Build(LargeIndex(), "What about it?", "What are the pool hours?", 4, 1500);

        Assert.Equal(KnowledgeContextMode.Excerpts, result.Mode);
        Assert.Equal(P("HOA", "Pool rules.md"), result.Excerpts[0].Path);
    }

    // ==================== Excerpts ====================

    [Fact]
    public void SelectExcerpts_KeepsTheMinimumThenOnlyGoodMatchesThatFit()
    {
        var hits = new[]
        {
            new KnowledgeHit(P("a.md"), 0, new string('a', 100), 10, 1),
            new KnowledgeHit(P("b.md"), 0, new string('b', 100), 6, 1),
            new KnowledgeHit(P("c.md"), 0, new string('c', 100), 2, 1), // below 35% of the best
            new KnowledgeHit(P("d.md"), 0, new string('d', 100), 1, 1),
        };

        Assert.Equal(2, KnowledgeContext.SelectExcerpts(hits, 1, 10_000).Count);
        // The minimum is kept even for weak matches...
        Assert.Equal(3, KnowledgeContext.SelectExcerpts(hits, 3, 10_000).Count);
        // ...but never past the room.
        Assert.Single(KnowledgeContext.SelectExcerpts(hits, 3, 150));
        Assert.Empty(KnowledgeContext.SelectExcerpts(hits, 3, 0));
    }

    [Fact]
    public void FormatExcerpts_GroupsByFileAndJoinsNeighbours()
    {
        var chunks = KnowledgeIndex.ChunkText(string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Sentence {i} of the bylaws text.")), 300, 80);
        Assert.True(chunks.Count >= 5);
        var hits = new[]
        {
            new KnowledgeHit(P("HOA", "Bylaws.md"), 1, chunks[1], 9, 1),
            new KnowledgeHit(P("Lease.md"), 0, "Rent is due on the first.", 8, 1),
            new KnowledgeHit(P("HOA", "Bylaws.md"), 0, chunks[0], 7, 1),
            new KnowledgeHit(P("HOA", "Bylaws.md"), 4, chunks[4], 6, 1),
        };

        var text = KnowledgeContext.FormatExcerpts(hits, Root);

        Assert.Contains("[1] " + Path.Combine("HOA", "Bylaws.md"), text);
        Assert.Contains("[2] Lease.md", text);
        Assert.True(text.IndexOf("[1]", StringComparison.Ordinal) < text.IndexOf("[2]", StringComparison.Ordinal));
        // Chunks 0 and 1 are one passage with no sentence twice; chunk 4 comes after a gap.
        Assert.Contains(KnowledgeContext.JoinChunks(new[] { chunks[0], chunks[1] }), text);
        Assert.Contains("\n[...]\n" + chunks[4], text);
        // The overlap between chunks 0 and 1 (chunk 1 starts with it) appears once.
        var firstOfChunk1 = chunks[1][..(chunks[1].IndexOf('.') + 1)];
        Assert.Contains(firstOfChunk1, chunks[0]);
        Assert.Equal(1, CountOf(text, firstOfChunk1));
    }

    // ==================== Joining chunks ====================

    [Fact]
    public void JoinChunks_RebuildsTheOriginalTextWithoutOverlap()
    {
        var text = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"Sentence number {i} talks about topic {i * 7}."));
        var paragraphs = string.Join("\n\n", Enumerable.Range(1, 15).Select(i => $"Paragraph {i}. " + string.Join(" ", Enumerable.Repeat($"Words of paragraph {i}.", 9))));

        Assert.Equal(text, KnowledgeContext.JoinChunks(KnowledgeIndex.ChunkText(text)));
        Assert.Equal(paragraphs, KnowledgeContext.JoinChunks(KnowledgeIndex.ChunkText(paragraphs)));
        Assert.Equal("", KnowledgeContext.JoinChunks(null));
        Assert.Equal("one\ntwo", KnowledgeContext.JoinChunks(new[] { "one", " ", "two" }));
    }

    [Fact]
    public void JoinChunks_RebuildsPdfFormRowsForViewText()
    {
        // Rows as the PDF reader lays them out: each a line, " | " between columns, no sentences to cut at.
        var rows = string.Join("\n", Enumerable.Range(1, 80).Select(i =>
            $"{i} Line {i} of the form, amount for item {i * 3} ... {i} | {i * 1234:N0}"));

        var chunks = KnowledgeIndex.ChunkText(rows);

        Assert.True(chunks.Count > 3);
        Assert.Equal(rows, KnowledgeContext.JoinChunks(chunks));
    }

    // ==================== Names ====================

    [Fact]
    public void FolderNameAndDocumentMentions()
    {
        Assert.Equal("townhouse", KnowledgeContext.FolderName(Root));
        Assert.Equal("townhouse", KnowledgeContext.FolderName(Root + Path.DirectorySeparatorChar));

        Assert.True(KnowledgeContext.MentionsDocuments("Summarize my documents", "townhouse"));
        Assert.True(KnowledgeContext.MentionsDocuments("what does the PDF say?", "townhouse"));
        Assert.True(KnowledgeContext.MentionsDocuments("Is the townhouse insured?", "townhouse"));
        Assert.True(KnowledgeContext.MentionsDocuments("anything about the townhouses", "townhouse"));
        Assert.False(KnowledgeContext.MentionsDocuments("Tell me a joke", "townhouse"));
        Assert.False(KnowledgeContext.MentionsDocuments("", "townhouse"));
        Assert.False(KnowledgeContext.MentionsDocuments("my house", "Town House Stuff"));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
