using System.Text;
using VoiceChatbot;
using Xunit;

public class KnowledgeIndexTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vc-knowledge-tests", "Docs");
    private static readonly DateTime Time1 = new(2026, 10, 1, 12, 30, 15, DateTimeKind.Utc);
    private static readonly DateTime Time2 = Time1.AddMinutes(5);

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static KnowledgeFileRecord Record(string path, params string[] chunks) => new()
    {
        Path = path,
        Size = 100,
        LastWriteUtc = Time1,
        Chunks = chunks.ToList()
    };

    private static KnowledgeIndex SampleIndex()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("Home", "Lease 2025.pdf"),
            "The tenant may keep one cat or one small dog. Pets need a deposit of 300 dollars.",
            "Rent is due on the first day of each month. Late rent costs a 50 dollar fee."));
        index.Upsert(Record(P("Car", "Honda manual.md"),
            "Change the engine oil every 5000 miles. Use 0W-20 synthetic oil.",
            "Tire pressure should be 35 psi when the tires are cold."));
        index.Upsert(Record(P("Recipes.txt"),
            "Pizza dough: flour, water, yeast and salt. Let the dough rise for two hours.",
            "Tomato soup with basil and garlic, simmered for thirty minutes."));
        return index;
    }

    // ==================== Chunking ====================

    [Fact]
    public void ChunkText_ShortAndEmptyText()
    {
        Assert.Empty(KnowledgeIndex.ChunkText(null));
        Assert.Empty(KnowledgeIndex.ChunkText("   \r\n\t "));
        Assert.Empty(KnowledgeIndex.ChunkText("---- *** ----"));
        Assert.Equal(new[] { "Hello there. Short note." }, KnowledgeIndex.ChunkText("  Hello \t  there.  Short note.  "));
        Assert.Equal(new[] { "Line one\n\nLine two" }, KnowledgeIndex.ChunkText("Line one  \r\n\r\n\r\n\r\n  Line two"));
    }

    [Fact]
    public void ChunkText_SplitsOnSentencesWithBoundedOverlap()
    {
        var sentences = Enumerable.Range(1, 80)
            .Select(i => $"Sentence number {i} talks about topic {i * 7} in some detail here.")
            .ToList();
        var text = string.Join(" ", sentences);

        var chunks = KnowledgeIndex.ChunkText(text);

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Length <= KnowledgeIndex.DefaultChunkChars, $"chunk too long: {c.Length}"));
        Assert.All(chunks.SkipLast(1), c => Assert.True(c.Length >= KnowledgeIndex.DefaultChunkChars / 2, $"chunk too short: {c.Length}"));
        // Chunks end on a sentence end and start on a sentence start.
        Assert.All(chunks, c => Assert.EndsWith(".", c));
        Assert.All(chunks, c => Assert.StartsWith("Sentence number ", c));
        // Every sentence survives whole in at least one chunk.
        Assert.All(sentences, s => Assert.Contains(chunks, c => c.Contains(s, StringComparison.Ordinal)));

        // Neighbours overlap, by at most the overlap size.
        for (var i = 1; i < chunks.Count; i++)
        {
            var firstSentence = chunks[i][..(chunks[i].IndexOf('.') + 1)];
            Assert.Contains(firstSentence, chunks[i - 1]);
            var overlapStart = chunks[i - 1].IndexOf(firstSentence, StringComparison.Ordinal);
            Assert.True(chunks[i - 1].Length - overlapStart <= KnowledgeIndex.DefaultOverlapChars);
        }
    }

    [Fact]
    public void ChunkText_PrefersParagraphBreaks()
    {
        var paragraphs = Enumerable.Range(1, 12)
            .Select(i => $"Paragraph {i}. " + string.Join(" ", Enumerable.Repeat($"Words of paragraph {i}.", 10)))
            .ToList();
        var text = string.Join("\n\n", paragraphs);

        var chunks = KnowledgeIndex.ChunkText(text);

        Assert.True(chunks.Count > 1);
        // Every chunk but the last ends exactly where a paragraph ends.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
            Assert.Contains(paragraphs, p => chunk.EndsWith(p, StringComparison.Ordinal));
        Assert.All(paragraphs, p => Assert.Contains(chunks, c => c.Contains(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void ChunkText_CutsTextWithoutBreaksAndAlwaysMovesForward()
    {
        var text = new string('x', 5000);

        var chunks = KnowledgeIndex.ChunkText(text, chunkChars: 900, overlapChars: 150);

        Assert.All(chunks, c => Assert.True(c.Length <= 900));
        // 5000 chars in steps of 900 - 150 = 750 => 7 chunks.
        Assert.Equal(7, chunks.Count);
        Assert.Equal(5000 + 150 * 6, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void ChunkText_WordsWithoutPunctuationBreakOnSpaces()
    {
        var text = string.Join(" ", Enumerable.Range(1, 600).Select(i => $"word{i}"));

        var chunks = KnowledgeIndex.ChunkText(text);

        Assert.All(chunks, c => Assert.Matches(@"^word\d+( word\d+)*$", c));
        Assert.Contains(chunks, c => c.EndsWith("word600", StringComparison.Ordinal));
    }

    [Fact]
    public void ChunkText_HonoursSmallSizes()
    {
        var text = string.Join(" ", Enumerable.Repeat("Alpha beta gamma delta.", 40));

        var chunks = KnowledgeIndex.ChunkText(text, chunkChars: 200, overlapChars: 0);

        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.Equal(text.Length, chunks.Sum(c => c.Length) + chunks.Count - 1);
    }

    // ==================== Planning ====================

    [Fact]
    public void Plan_ReadsNewAndChangedFilesAndDropsDeletedOnes()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("same.txt"), "unchanged"));
        index.Upsert(Record(P("resized.txt"), "old"));
        index.Upsert(Record(P("touched.txt"), "old"));
        index.Upsert(Record(P("deleted.txt"), "gone"));

        var plan = index.Plan(new[]
        {
            new KnowledgeFileStamp(P("same.txt"), 100, Time1),
            new KnowledgeFileStamp(P("resized.txt"), 101, Time1),
            new KnowledgeFileStamp(P("touched.txt"), 100, Time2),
            new KnowledgeFileStamp(P("new.md"), 5, Time1),
            new KnowledgeFileStamp(P("NEW.md"), 5, Time1), // same file, other case (Windows)
        });

        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(new[] { P("resized.txt"), P("touched.txt"), P("new.md") }, plan.ToRead.Select(f => f.Path));
        Assert.Equal(new[] { P("deleted.txt") }, plan.Removed);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void Plan_TreatsLocalAndUtcTimesAlike_AndRetriesFailedFilesOnRequest()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("good.txt"), "text"));
        index.Upsert(new KnowledgeFileRecord { Path = P("scan.pdf"), Size = 100, LastWriteUtc = Time1, Error = "No readable text" });
        var scan = new[]
        {
            new KnowledgeFileStamp(P("good.txt"), 100, Time1.ToLocalTime()),
            new KnowledgeFileStamp(P("scan.pdf"), 100, Time1),
        };

        var automatic = index.Plan(scan);
        var manual = index.Plan(scan, retryFailed: true);

        Assert.False(automatic.HasChanges);
        Assert.Equal(2, automatic.Unchanged);
        Assert.Equal(new[] { P("scan.pdf") }, manual.ToRead.Select(f => f.Path));
    }

    [Fact]
    public void UpsertRemoveAndCounts()
    {
        var index = SampleIndex();
        index.Upsert(new KnowledgeFileRecord { Path = P("broken.docx"), Error = "Corrupt" });

        Assert.Equal(4, index.FileCount);
        Assert.Equal(3, index.IndexedFileCount);
        Assert.Equal(1, index.UnreadableFileCount);
        Assert.Equal(6, index.ChunkCount);

        Assert.True(index.TryGetFile(P("BROKEN.docx"), out var broken));
        Assert.Equal("Corrupt", broken.Error);
        Assert.False(index.TryGetFile(P("missing.txt"), out _));

        Assert.True(index.Remove(P("broken.docx")));
        Assert.False(index.Remove(P("broken.docx")));
        Assert.False(index.Remove(""));
        Assert.Equal(3, index.FileCount);
        Assert.Throws<ArgumentException>(() => index.Upsert(new KnowledgeFileRecord()));
    }

    // ==================== Search ====================

    [Fact]
    public void Search_FindsTheMatchingChunk()
    {
        var index = SampleIndex();

        var hits = index.Search("How often should I change the engine oil?");

        Assert.NotEmpty(hits);
        Assert.Equal(P("Car", "Honda manual.md"), hits[0].Path);
        Assert.Equal(0, hits[0].ChunkIndex);
        Assert.Contains("5000 miles", hits[0].Text);
        Assert.Equal("Honda manual.md", hits[0].FileName);
        Assert.True(hits[0].Coverage >= KnowledgeIndex.DefaultMinCoverage);
    }

    [Fact]
    public void Search_IgnoresFillerAndSmallTalk()
    {
        var index = SampleIndex();
        index.Upsert(Record(P("diary.txt"), "Thanks for a good morning. What time is the weather show today? Tell me."));

        Assert.Empty(index.Search("Thanks, good morning!"));
        Assert.Empty(index.Search("What time is it today?"));
        Assert.Empty(index.Search("Tell me what the documents say"));
        Assert.Empty(index.Search(""));
        Assert.Empty(index.Search(null));
        // Filler words are dropped but the topic still matches.
        Assert.Equal(P("Home", "Lease 2025.pdf"), index.Search("What does my document say about the pet deposit?")[0].Path);
    }

    [Fact]
    public void Search_SkipsChunksThatShareOnlyAFewWords()
    {
        var index = SampleIndex();

        // "dough" matches the recipe, but "sourdough starter feeding schedule kombucha" is not in any file.
        Assert.Empty(index.Search("sourdough starter feeding schedule kombucha dough"));
        Assert.NotEmpty(index.Search("pizza dough rise"));
    }

    // A small realistic folder: questions about it find the right file, small talk and general
    // questions find nothing.
    private static KnowledgeIndex PersonalIndex()
    {
        var docs = new Dictionary<string, string>
        {
            [P("Lease Agreement.pdf")] = "RESIDENTIAL LEASE AGREEMENT\n\nThis lease is made between Oak Street Properties (Landlord) and the Tenant for the premises at 42 Oak Street, Apt 3.\n\n1. Term. The lease begins on March 1, 2025 and ends on February 28, 2026.\n\n2. Rent. Monthly rent is $1,850, due on the first day of each month. A late fee of $75 applies after the fifth day.\n\n3. Security Deposit. Tenant paid a security deposit of $1,850, returned within 30 days after move-out minus damages.\n\n4. Pets. One cat or one dog under 40 pounds is allowed with a $300 pet deposit. No reptiles.\n\n5. Maintenance. Tenant must replace HVAC filters every three months and report leaks within 24 hours. Landlord handles appliance repairs.\n\n6. Termination. Either party may terminate with 60 days written notice. Early termination by the tenant costs two months of rent.\n\n7. Parking. One assigned space, number 14. Guests park on the street.",
            [P("Car", "2019 Honda CR-V maintenance.md")] = "# Honda CR-V maintenance\n\nOil: 0W-20 full synthetic, change every 7,500 miles.\n\nTires: 35 psi front and rear when cold. Rotate every 7,500 miles.\n\nBattery: Group 51R, installed May 2023, 3 year warranty.",
            [P("Health", "Dentist.txt")] = "Dr. Patel, Bright Smile Dental, 555-0142. Next cleaning appointment: November 12 at 9:30am. Insurance: Delta Dental PPO, member ID DD-449201.",
            [P("Recipes", "Grandma's chili.txt")] = "Grandma's chili. Brown 2 lb ground beef with one onion. Add two cans kidney beans, one can crushed tomatoes, 3 tbsp chili powder. Simmer 2 hours. Serve with cornbread.",
            [P("Home", "WiFi and network.txt")] = "Home network. WiFi name: Gagnon-5G. Password: purple-otter-42. Router admin at 192.168.1.1.",
        };

        var index = new KnowledgeIndex(Root);
        foreach (var (path, text) in docs)
            index.Upsert(new KnowledgeFileRecord { Path = path, Chunks = KnowledgeIndex.ChunkText(text) });
        return index;
    }

    [Theory]
    [InlineData("What's the wifi password?", "WiFi and network.txt")]
    [InlineData("When is my dentist appointment?", "Dentist.txt")]
    [InlineData("What's my insurance member ID", "Dentist.txt")]
    [InlineData("What tire pressure for my car?", "2019 Honda CR-V maintenance.md")] // "Car" is its folder
    [InlineData("How much notice do I need to give to end my lease?", "Lease Agreement.pdf")]
    [InlineData("Where do my guests park", "Lease Agreement.pdf")]
    [InlineData("What's in grandma's chili", "Grandma's chili.txt")]
    public void Search_PersonalQuestionsFindTheirFile(string question, string expectedFile)
    {
        var hits = PersonalIndex().Search(question);

        Assert.NotEmpty(hits);
        Assert.Equal(expectedFile, hits[0].FileName);
    }

    [Theory]
    [InlineData("What time is it?")]
    [InlineData("Tell me a joke")]
    [InlineData("Hey, how are you doing?")]
    [InlineData("Good night")]
    [InlineData("What is the capital of France?")]
    [InlineData("Write a python script to sort a list")]
    [InlineData("What should I name my cat?")] // shares only "name" or "cat" with a file
    [InlineData("What's a good pizza recipe?")] // shares only the "Recipes" folder name
    [InlineData("How do I make cornbread?")]
    public void Search_SmallTalkAndUnrelatedQuestionsFindNothing(string question) =>
        Assert.Empty(PersonalIndex().Search(question));

    [Fact]
    public void Search_LimitsAndOrdersResults()
    {
        var index = new KnowledgeIndex(Root);
        for (var i = 0; i < 6; i++)
            index.Upsert(Record(P($"solar{i}.txt"), $"Solar panel notes {i}." + string.Concat(Enumerable.Repeat(" solar", i))));

        var hits = index.Search("solar panel", maxChunks: 3);

        Assert.Equal(3, hits.Count);
        Assert.True(hits[0].Score >= hits[1].Score && hits[1].Score >= hits[2].Score);
        Assert.Empty(index.Search("solar panel", maxChunks: 0));
    }

    [Fact]
    public void Search_MatchesFileNames()
    {
        var index = SampleIndex();

        var hits = index.Search("When is the lease rent due?");

        Assert.Equal(P("Home", "Lease 2025.pdf"), hits[0].Path);
        Assert.Equal(1, hits[0].ChunkIndex);
    }

    // A pool rules file and a newsletter that says "pool" far more often.
    private static KnowledgeIndex PoolIndex()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("HOA", "Pool rules.md"), "Open 9am to 8pm daily. Children under 12 need an adult. Hours may change in winter."));
        index.Upsert(Record(P("Newsletter.md"), "Pool party! The pool is busy, pool toys are welcome and the pool hours will be posted at the pool."));
        index.Upsert(Record(P("Lease.md"), "Rent is due on the first. Parking space 14 is assigned to the tenant."));
        index.Upsert(Record(P("Car", "Manual.md"), "Change the engine oil every 5000 miles."));
        return index;
    }

    [Fact]
    public void Search_FileAndFolderNamesBoostTheirChunks()
    {
        var index = PoolIndex();

        // Without the name boost the newsletter's many "pool"s win; with it, the file named for the topic does.
        Assert.Equal(P("Newsletter.md"), index.Search("pool hours", nameBoost: 0)[0].Path);
        var hits = index.Search("pool hours");
        Assert.Equal(P("HOA", "Pool rules.md"), hits[0].Path);
        Assert.True(hits[0].Score > index.Search("pool hours", nameBoost: 0).Single(h => h.Path == P("HOA", "Pool rules.md")).Score);
        // Only the names that hold query words are boosted.
        Assert.Equal(index.Search("pool hours", nameBoost: 0).Single(h => h.Path == P("Newsletter.md")).Score,
            hits.Single(h => h.Path == P("Newsletter.md")).Score, 9);
    }

    [Fact]
    public void Search_FollowUpRanksChunksThatAlsoMatchThePreviousQuestion()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(Record(P("HOA", "Bylaws.md"), "Article 7. Each unit has two parking spaces. Guests use the visitor lot."));
        index.Upsert(Record(P("Lease.md"), "Parking: space 14 is assigned. Parking parking parking."));
        index.Upsert(Record(P("Car", "Manual.md"), "Change the engine oil every 5000 miles."));

        Assert.Equal(P("Lease.md"), index.Search("and parking?")[0].Path);
        Assert.Equal(P("HOA", "Bylaws.md"), index.Search("and parking?", followUpContext: "What do the HOA bylaws say about pets?")[0].Path);
        // The previous question only ranks; it does not add chunks that miss the follow-up's own words.
        Assert.DoesNotContain(index.Search("and parking?", followUpContext: "engine oil"), h => h.Path == P("Car", "Manual.md"));
    }

    [Fact]
    public void Search_AMessageWithoutWordsOfItsOwnSearchesForThePreviousQuestion()
    {
        var index = SampleIndex();

        Assert.Empty(index.Search("What about it?"));
        Assert.Equal(P("Car", "Honda manual.md"), index.Search("What about it?", followUpContext: "How often do I change the engine oil?")[0].Path);
        Assert.Equal(P("Car", "Honda manual.md"), index.Search("Tell me more", followUpContext: "engine oil")[0].Path);
    }

    [Theory]
    [InlineData("and the parking rules?", true)]
    [InlineData("what about it?", true)]
    [InlineData("Tell me more", true)]
    [InlineData("What does the HOA say about guest parking at the visitor lot?", false)]
    [InlineData("How often should I change the engine oil in my car?", false)]
    public void IsShortFollowUp(string text, bool expected) => Assert.Equal(expected, KnowledgeIndex.IsShortFollowUp(text));

    [Fact]
    public void Search_SummarizeAndFileTypeWordsAreFiller()
    {
        var index = SampleIndex();

        Assert.Equal(P("Home", "Lease 2025.pdf"), index.Search("Summarize the lease pdf")[0].Path);
        Assert.Empty(index.Search("summarize the pdf"));
    }

    [Fact]
    public void FilesInOrder_SplitsReadableAndUnreadable()
    {
        var index = SampleIndex();
        index.Upsert(new KnowledgeFileRecord { Path = P("Scan.pdf"), Error = "No readable text." });

        Assert.Equal(new[] { P("Car", "Honda manual.md"), P("Home", "Lease 2025.pdf"), P("Recipes.txt") },
            index.IndexedFilesInOrder().Select(f => f.Path));
        Assert.Equal(new[] { P("Scan.pdf") }, index.UnreadableFilesInOrder().Select(f => f.Path));
    }

    [Fact]
    public void Search_SeesChangesAfterUpsertAndRemove()
    {
        var index = SampleIndex();
        Assert.Empty(index.Search("dentist appointment"));

        index.Upsert(Record(P("Health.md"), "Dentist appointment on March 3 at 9am with Dr. Lee."));
        Assert.Equal(P("Health.md"), index.Search("dentist appointment").Single().Path);

        index.Remove(P("Health.md"));
        Assert.Empty(index.Search("dentist appointment"));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var index = SampleIndex();
        index.LastIndexedUtc = Time2;

        var copy = index.Clone();
        copy.Remove(P("Recipes.txt"));

        Assert.Equal(3, index.FileCount);
        Assert.Equal(2, copy.FileCount);
        Assert.Equal(Time2, copy.LastIndexedUtc);
        Assert.Equal(index.Folder, copy.Folder);
        Assert.NotEmpty(index.Search("tomato soup basil"));
        Assert.Empty(copy.Search("tomato soup basil"));
    }

    // ==================== Prompt text ====================

    [Fact]
    public void FormatExcerptsAndNote()
    {
        var index = SampleIndex();
        var hits = index.Search("engine oil tire pressure", maxChunks: 4);

        var context = KnowledgeContext.FormatExcerpts(hits, Root);
        var note = KnowledgeContext.FormatNote(hits);

        Assert.StartsWith("Excerpts found by keyword search", context);
        Assert.Contains("[1] " + Path.Combine("Car", "Honda manual.md"), context);
        Assert.Contains("5000 miles", context);
        Assert.Equal($"Using {hits.Count} excerpts from: Honda manual.md", note);

        var single = new[] { new KnowledgeHit(P("a.pdf"), 0, "text", 1, 1) };
        Assert.Equal("Using 1 excerpt from: a.pdf", KnowledgeContext.FormatNote(single));
        Assert.Equal("", KnowledgeContext.FormatNote(Array.Empty<KnowledgeHit>()));
        Assert.Equal("", KnowledgeContext.FormatExcerpts(Array.Empty<KnowledgeHit>()));
    }

    [Fact]
    public void FormatNote_ListsEachFileOnceInRankOrder()
    {
        var hits = new[]
        {
            new KnowledgeHit(P("b.md"), 0, "x", 3, 1),
            new KnowledgeHit(P("a.pdf"), 1, "y", 2, 1),
            new KnowledgeHit(P("b.md"), 2, "z", 1, 1),
        };

        Assert.Equal("Using 3 excerpts from: b.md, a.pdf", KnowledgeContext.FormatNote(hits));
    }

    [Fact]
    public void FormatNote_NamesAtMostThreeFiles()
    {
        var hits = Enumerable.Range(1, 5).Select(i => new KnowledgeHit(P($"f{i}.txt"), 0, "x", 10 - i, 1)).ToList();

        Assert.Equal("Using 5 excerpts from: f1.txt, f2.txt, f3.txt and 2 more", KnowledgeContext.FormatNote(hits));
    }

    [Fact]
    public void DisplayName_IsRelativeInsideTheFolder()
    {
        Assert.Equal(Path.Combine("Home", "Lease.pdf"), KnowledgeIndex.DisplayName(P("Home", "Lease.pdf"), Root));
        Assert.Equal("Lease.pdf", KnowledgeIndex.DisplayName(P("Home", "Lease.pdf"), ""));
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.txt");
        Assert.Equal("x.txt", KnowledgeIndex.DisplayName(outside, Root));
    }

    [Fact]
    public void NormalizeAndCompareFolders()
    {
        Assert.Equal("", KnowledgeIndex.NormalizeFolder("  "));
        Assert.Equal(Root, KnowledgeIndex.NormalizeFolder(Root + Path.DirectorySeparatorChar));
        Assert.Equal(Root, KnowledgeIndex.NormalizeFolder($"\"{Root}\""));
        Assert.True(KnowledgeIndex.SameFolder(Root, Root + Path.DirectorySeparatorChar));
        Assert.True(KnowledgeIndex.SameFolder(Root, Root.ToUpperInvariant()));
        Assert.False(KnowledgeIndex.SameFolder("", ""));
        Assert.False(KnowledgeIndex.SameFolder(Root, P("Home")));
    }

    [Fact]
    public void ClampMaxChunks()
    {
        Assert.Equal(1, KnowledgeIndex.ClampMaxChunks(0));
        Assert.Equal(4, KnowledgeIndex.ClampMaxChunks(4));
        Assert.Equal(KnowledgeIndex.MaxChunks, KnowledgeIndex.ClampMaxChunks(99));
    }

    // ==================== JSON ====================

    [Fact]
    public void Json_RoundTripsRecordsAndKeepsFilesUnchanged()
    {
        var index = SampleIndex();
        index.Upsert(new KnowledgeFileRecord { Path = P("scan.pdf"), Size = 7, LastWriteUtc = Time2, Error = "OCR unavailable" });
        index.LastIndexedUtc = Time2;

        var loaded = KnowledgeIndex.FromJson(index.ToJson());

        Assert.Equal(index.Folder, loaded.Folder);
        Assert.Equal(Time2, loaded.LastIndexedUtc);
        Assert.Equal(4, loaded.FileCount);
        Assert.Equal(6, loaded.ChunkCount);
        var scan = loaded.Files.Single(f => f.Path == P("scan.pdf"));
        Assert.Equal("OCR unavailable", scan.Error);
        Assert.Equal(7, scan.Size);
        Assert.Empty(scan.Chunks);

        var plan = loaded.Plan(index.Files.Select(f => new KnowledgeFileStamp(f.Path, f.Size, f.LastWriteUtc)));
        Assert.False(plan.HasChanges);
        Assert.Equal(index.Search("engine oil")[0].Text, loaded.Search("engine oil")[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"version\": 99, \"folder\": \"x\", \"files\": []}")]
    [InlineData("{\"folder\": \"x\"}")]
    public void Json_BadInputGivesAnEmptyIndex(string? json)
    {
        var index = KnowledgeIndex.FromJson(json);

        Assert.Equal(0, index.FileCount);
        Assert.Equal("", index.Folder);
        Assert.Null(index.LastIndexedUtc);
    }

    [Fact]
    public void Json_SkipsBrokenRecords()
    {
        var json = "{\"version\":1,\"folder\":\"\",\"files\":[null,{\"path\":\"\"},{\"path\":\"a.txt\",\"chunks\":null},{\"path\":\"b.txt\",\"chunks\":[\"greenhouse\",\" \",null]}]}";

        var index = KnowledgeIndex.FromJson(json);

        Assert.Equal(2, index.FileCount);
        Assert.Equal(1, index.ChunkCount);
        Assert.Equal("b.txt", index.Search("greenhouse").Single().Path);
    }

    [Fact]
    public void Json_HandlesLargeIndexes()
    {
        var index = new KnowledgeIndex(Root);
        var text = new StringBuilder();
        for (var i = 0; i < 400; i++)
            text.Append($"Item {i} describes widget model W{i} with \"quotes\" and a back\\slash. ");
        index.Upsert(new KnowledgeFileRecord { Path = P("big.txt"), Size = text.Length, LastWriteUtc = Time1, Chunks = KnowledgeIndex.ChunkText(text.ToString()) });

        var loaded = KnowledgeIndex.FromJson(index.ToJson());

        Assert.Equal(index.ChunkCount, loaded.ChunkCount);
        Assert.Contains("W123", loaded.Search("widget model W123")[0].Text);
    }
}
