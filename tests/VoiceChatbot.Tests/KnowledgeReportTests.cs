using System.Globalization;
using VoiceChatbot;
using Xunit;

public class KnowledgeReportTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "vc-knowledge-report-tests", "townhouse");
    private static readonly DateTime Checked = new(2026, 10, 9, 4, 52, 0, DateTimeKind.Utc);

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static IReadOnlyList<KnowledgeExtensionCount> TownhouseSkipped() =>
        KnowledgeReport.CountByExtension(
            Enumerable.Repeat(P("photo.JPG"), 12)
                .Concat(Enumerable.Repeat(P("budget.xlsx"), 6))
                .Concat(Enumerable.Repeat(P("old.doc"), 5)));

    private static KnowledgeReindexResult Result(int filesRead = 2) => new()
    {
        Outcome = KnowledgeReindexOutcome.Completed,
        Folder = Root,
        IndexedFiles = 18,
        Chunks = 412,
        FilesToRead = filesRead,
        FilesRead = filesRead,
        Unreadable = new[] { new KnowledgeFileEntry(P("Scans", "scan.pdf"), KnowledgeFileState.Unreadable, Reason: "No readable text.") },
        Skipped = TownhouseSkipped(),
        CheckedUtc = Checked
    };

    private static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    // ==================== Skipped files ====================

    [Fact]
    public void CountByExtension_GroupsLowercasedMostCommonFirst()
    {
        var counts = KnowledgeReport.CountByExtension(new[] { "a.JPG", "b.jpg", "c.xlsx", "README", "d.zip", "e.zip", "f.jpg" });

        Assert.Equal(new[]
        {
            new KnowledgeExtensionCount(".jpg", 3),
            new KnowledgeExtensionCount(".zip", 2),
            new KnowledgeExtensionCount(".xlsx", 1),
            new KnowledgeExtensionCount("", 1),
        }, counts);
        Assert.Empty(KnowledgeReport.CountByExtension(null));
    }

    [Fact]
    public void AddToCounts_MatchesCountByExtension()
    {
        var running = new Dictionary<string, int>();
        foreach (var path in new[] { "a.png", "b.PNG", "c.mov" })
            KnowledgeReport.AddToCounts(running, path);

        Assert.Equal(KnowledgeReport.CountByExtension(new[] { "a.png", "b.PNG", "c.mov" }), KnowledgeReport.SortCounts(running));
    }

    [Fact]
    public void FormatExtensionCounts_NamesTheTopThreeAndCountsTheRest()
    {
        Assert.Equal("12 .jpg, 6 .xlsx, 5 .doc", KnowledgeReport.FormatExtensionCounts(TownhouseSkipped()));
        Assert.Equal("23 skipped: 12 .jpg, 6 .xlsx, 5 .doc", KnowledgeReport.FormatSkippedStatus(TownhouseSkipped()));

        // A fourth group is named rather than called "1 other".
        var four = TownhouseSkipped().Append(new KnowledgeExtensionCount(".zip", 1)).ToList();
        Assert.Equal("12 .jpg, 6 .xlsx, 5 .doc, 1 .zip", KnowledgeReport.FormatExtensionCounts(four));

        var five = four.Append(new KnowledgeExtensionCount("", 2)).ToList();
        Assert.Equal("12 .jpg, 6 .xlsx, 5 .doc, 3 other", KnowledgeReport.FormatExtensionCounts(five));
        Assert.Equal("1 without extension", KnowledgeReport.FormatExtensionCounts(new[] { new KnowledgeExtensionCount("", 1) }));

        Assert.Equal("", KnowledgeReport.FormatExtensionCounts(null));
        Assert.Equal("", KnowledgeReport.FormatSkippedStatus(Array.Empty<KnowledgeExtensionCount>()));
    }

    // ==================== Status line and tooltip ====================

    [Fact]
    public void FormatStatus_SaysWhatWasIndexedAndWhatWasLeftOut()
    {
        var result = Result() with
        {
            TooLarge = new[] { new KnowledgeFileEntry(P("video.pdf"), KnowledgeFileState.TooLarge, Size: 40L * 1024 * 1024) }
        };

        var status = KnowledgeReport.FormatStatus(result, Checked.ToLocalTime());

        Assert.Equal($"18 files, 412 chunks · checked today {LocalTime(Checked)} · 1 could not be read · " +
                     "23 skipped: 12 .jpg, 6 .xlsx, 5 .doc · 1 over 25 MB skipped", status);
        Assert.StartsWith("1 file, 1 chunk", KnowledgeReport.FormatStatus(new KnowledgeReindexResult { IndexedFiles = 1, Chunks = 1 }, DateTime.Now));
        Assert.Contains("index full", KnowledgeReport.FormatStatus(Result() with { IndexFull = true }, DateTime.Now));
        Assert.Contains("stopped at 10,000 files", KnowledgeReport.FormatStatus(Result() with { ScanTruncated = true }, DateTime.Now));
    }

    [Fact]
    public void FormatDetails_ExplainsTheStatusLine()
    {
        var details = KnowledgeReport.FormatDetails(Result());

        Assert.Contains("Could not read (Reindex tries these again):\n" + Path.Combine("Scans", "scan.pdf") + ": No readable text.", details);
        Assert.Contains("Skipped, type not supported: 12 .jpg, 6 .xlsx, 5 .doc", details);
        Assert.EndsWith("Click Files for the full list.", details);
        Assert.Equal("", KnowledgeReport.FormatDetails(new KnowledgeReindexResult { IndexedFiles = 3, Chunks = 9 }));
    }

    [Fact]
    public void FormatDetails_ListsAtMostTheLimit()
    {
        var unreadable = Enumerable.Range(1, 14).Select(i => new KnowledgeFileEntry(P($"scan{i:00}.pdf"), KnowledgeFileState.Unreadable)).ToList();

        var details = KnowledgeReport.FormatDetails(new KnowledgeReindexResult { Folder = Root, Unreadable = unreadable }, maxListed: 10);

        Assert.Contains("scan10.pdf", details);
        Assert.DoesNotContain("scan11.pdf", details);
        Assert.Contains("...and 4 more", details);
    }

    [Theory]
    [InlineData(null, "Reading 3 of 25: HOA Bylaws.pdf")]
    [InlineData("OCR page 2 of 8...", "Reading 3 of 25: HOA Bylaws.pdf · OCR page 2 of 8")]
    [InlineData("rendering scanned pages for OCR...", "Reading 3 of 25: HOA Bylaws.pdf · rendering scanned pages for OCR")]
    [InlineData("reading PDF text...", "Reading 3 of 25: HOA Bylaws.pdf")]
    [InlineData("   ", "Reading 3 of 25: HOA Bylaws.pdf")]
    public void FormatReadingProgress(string? detail, string expected) =>
        Assert.Equal(expected, KnowledgeReport.FormatReadingProgress(3, 25, "HOA Bylaws.pdf", detail));

    // ==================== Chat summary ====================

    [Fact]
    public void FormatChatSummary_AfterChanges()
    {
        Assert.Equal(
            "Knowledge folder: 18 files indexed (412 chunks), 2 new or changed. Skipped 23 (12 .jpg, 6 .xlsx, 5 .doc: not supported). " +
            "Could not read 1: scan.pdf (no text).",
            KnowledgeReport.FormatChatSummary(Result()));

        var removed = Result() with { Removed = 3, Unreadable = Array.Empty<KnowledgeFileEntry>(), Skipped = Array.Empty<KnowledgeExtensionCount>() };
        Assert.Equal("Knowledge folder: 18 files indexed (412 chunks), 2 new or changed, 3 removed.", KnowledgeReport.FormatChatSummary(removed));
    }

    [Fact]
    public void FormatChatSummary_WhenNothingChanged()
    {
        var upToDate = Result(filesRead: 0) with { Unreadable = Array.Empty<KnowledgeFileEntry>(), Skipped = Array.Empty<KnowledgeExtensionCount>() };

        Assert.Equal($"Knowledge folder is up to date: 18 files (412 chunks), checked {LocalTime(Checked)}.",
            KnowledgeReport.FormatChatSummary(upToDate));
        // What was left out is repeated, so the reason the model knows little stays visible.
        Assert.Contains("Skipped 23 (12 .jpg, 6 .xlsx, 5 .doc: not supported).", KnowledgeReport.FormatChatSummary(Result(filesRead: 0)));
    }

    [Fact]
    public void FormatChatSummary_NamesAtMostThreeUnreadableFiles()
    {
        var unreadable = new[]
        {
            new KnowledgeFileEntry(P("d.pdf"), KnowledgeFileState.Unreadable, Reason: "OCR unavailable: could not find tesseract.exe."),
            new KnowledgeFileEntry(P("a.pdf"), KnowledgeFileState.Unreadable, Reason: "No readable text was found. If this is a scanned PDF, OCR did not produce usable text."),
            new KnowledgeFileEntry(P("c.docx"), KnowledgeFileState.Unreadable, Reason: "File not found."),
            new KnowledgeFileEntry(P("b.pdf"), KnowledgeFileState.Unreadable),
        };

        var summary = KnowledgeReport.FormatChatSummary(Result() with { Unreadable = unreadable, Skipped = Array.Empty<KnowledgeExtensionCount>() });

        Assert.EndsWith("Could not read 4: a.pdf (no text), b.pdf (no text), c.docx (file not found) and 1 more.", summary);
    }

    [Fact]
    public void FormatChatSummary_OtherOutcomes()
    {
        Assert.Equal("", KnowledgeReport.FormatChatSummary(new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.NoFolder }));
        Assert.Equal($"Knowledge folder not found: {Root}. Choose the folder again with Browse.",
            KnowledgeReport.FormatChatSummary(new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.FolderNotFound, Folder = Root }));
        Assert.Equal("Knowledge folder: indexing failed: Access denied.",
            KnowledgeReport.FormatChatSummary(new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.Failed, Error = "Access denied" }));

        var stopped = new KnowledgeReindexResult
        {
            Outcome = KnowledgeReindexOutcome.Stopped,
            IndexedFiles = 7,
            Chunks = 80,
            FilesToRead = 25,
            FilesRead = 5
        };
        Assert.Equal("Knowledge folder: indexing stopped after 5 of 25 new or changed files. 7 files (80 chunks) can be searched; Reindex carries on from there.",
            KnowledgeReport.FormatChatSummary(stopped));

        var nothing = new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.Completed, Skipped = TownhouseSkipped() };
        Assert.Equal("Knowledge folder is up to date: 0 files (0 chunks). Skipped 23 (12 .jpg, 6 .xlsx, 5 .doc: not supported). Nothing in this folder can be searched yet.",
            KnowledgeReport.FormatChatSummary(nothing));
    }

    [Theory]
    [InlineData(null, "no text")]
    [InlineData("", "no text")]
    [InlineData("No readable text.", "no text")]
    [InlineData("File not found.", "file not found")]
    [InlineData("OCR unavailable: could not find pdftoppm.exe from Poppler.", "OCR unavailable: could not find pdftoppm.exe from Poppler")]
    [InlineData("Old .doc files are not supported yet. Save it as .docx and attach that.", "old .doc files are not supported yet")]
    public void ShortReason(string? error, string expected) => Assert.Equal(expected, KnowledgeReport.ShortReason(error));

    [Fact]
    public void ShortReason_CutsLongErrors()
    {
        var reason = KnowledgeReport.ShortReason(new string('x', 200), maxChars: 40);

        Assert.Equal(40, reason.Length);
        Assert.EndsWith("...", reason);
    }

    // ==================== Files list ====================

    [Fact]
    public void DescribeEntry_ForEachState()
    {
        Assert.Equal("Indexed - 12 chunks", KnowledgeReport.DescribeEntry(new(P("a.md"), KnowledgeFileState.Indexed, Chunks: 12)));
        Assert.Equal("Indexed - 1 chunk", KnowledgeReport.DescribeEntry(new(P("a.md"), KnowledgeFileState.Indexed, Chunks: 1)));
        Assert.Equal("Could not read - no text", KnowledgeReport.DescribeEntry(new(P("a.pdf"), KnowledgeFileState.Unreadable, Reason: "No readable text.")));
        Assert.Equal("Skipped - type not supported", KnowledgeReport.DescribeEntry(new(P("a.jpg"), KnowledgeFileState.Unsupported)));
        Assert.Equal("Too large - 30 MB (limit 25 MB)", KnowledgeReport.DescribeEntry(new(P("a.pdf"), KnowledgeFileState.TooLarge, Size: 30L * 1024 * 1024)));
        Assert.Equal("Not read yet - indexing stopped", KnowledgeReport.DescribeEntry(new(P("a.pdf"), KnowledgeFileState.Pending, Reason: "indexing stopped")));
    }

    [Fact]
    public void Sort_ByStatusPutsProblemsFirst()
    {
        var entries = new[]
        {
            new KnowledgeFileEntry(P("b.md"), KnowledgeFileState.Indexed, 3),
            new KnowledgeFileEntry(P("z.jpg"), KnowledgeFileState.Unsupported),
            new KnowledgeFileEntry(P("a.md"), KnowledgeFileState.Indexed, 1),
            new KnowledgeFileEntry(P("big.pdf"), KnowledgeFileState.TooLarge),
            new KnowledgeFileEntry(P("scan.pdf"), KnowledgeFileState.Unreadable),
            new KnowledgeFileEntry(P("c.xlsx"), KnowledgeFileState.Unsupported),
        };

        string Names(IEnumerable<KnowledgeFileEntry> sorted) => string.Join(" ", sorted.Select(e => Path.GetFileName(e.Path)));

        Assert.Equal("scan.pdf big.pdf c.xlsx z.jpg a.md b.md", Names(KnowledgeReport.Sort(entries, KnowledgeFileSort.Status, folder: Root)));
        Assert.Equal("b.md a.md z.jpg c.xlsx big.pdf scan.pdf", Names(KnowledgeReport.Sort(entries, KnowledgeFileSort.Status, descending: true, folder: Root)));
        Assert.Equal("a.md b.md big.pdf c.xlsx scan.pdf z.jpg", Names(KnowledgeReport.Sort(entries, KnowledgeFileSort.Name, folder: Root)));
        Assert.Equal("z.jpg a.md b.md big.pdf scan.pdf c.xlsx", Names(KnowledgeReport.Sort(entries, KnowledgeFileSort.Type, folder: Root)));
        Assert.Empty(KnowledgeReport.Sort(null, KnowledgeFileSort.Status));
    }

    [Fact]
    public void BuildFileList_CombinesTheIndexAndTheScan()
    {
        var index = new KnowledgeIndex(Root);
        index.Upsert(new KnowledgeFileRecord { Path = P("a.md"), Size = 10, Chunks = new List<string> { "one", "two" } });
        index.Upsert(new KnowledgeFileRecord { Path = P("scan.pdf"), Size = 20, Error = "OCR unavailable" });
        var scan = new[]
        {
            new KnowledgeFileEntry(P("photo.jpg"), KnowledgeFileState.Unsupported, Size: 5),
            new KnowledgeFileEntry(P("a.md"), KnowledgeFileState.Pending), // already in the index
        };

        var list = KnowledgeReport.BuildFileList(index, scan);

        Assert.Equal(3, list.Count);
        Assert.Contains(list, e => e.Path == P("a.md") && e.State == KnowledgeFileState.Indexed && e.Chunks == 2);
        Assert.Contains(list, e => e.Path == P("scan.pdf") && e.State == KnowledgeFileState.Unreadable && e.Reason == "OCR unavailable");
        Assert.Contains(list, e => e.Path == P("photo.jpg") && e.State == KnowledgeFileState.Unsupported);
        Assert.Empty(KnowledgeReport.BuildFileList(null, null));

        Assert.Equal("1 indexed · 1 could not be read · 4 skipped (type not supported)", KnowledgeReport.FormatFileCounts(list, unlistedSkipped: 3));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(26214400, "25 MB")]
    [InlineData(27787264, "26.5 MB")]
    public void FormatSize(long bytes, string expected) =>
        Assert.Equal(expected, KnowledgeReport.FormatSize(bytes).Replace(',', '.'));
}
