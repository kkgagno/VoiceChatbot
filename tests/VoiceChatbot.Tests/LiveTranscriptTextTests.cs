using VoiceChatbot;
using Xunit;

public class LiveTranscriptTextTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(7.9, "00:07")]
    [InlineData(65, "01:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3723, "1:02:03")]
    [InlineData(36000, "10:00:00")]
    [InlineData(-5, "00:00")]
    public void FormatsElapsedTime(double seconds, string expected)
    {
        Assert.Equal(expected, LiveTranscriptText.FormatTimestamp(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatsALineWithItsTimestamp()
    {
        Assert.Equal("[02:05] Hello there. How are you?",
            LiveTranscriptText.FormatLine(TimeSpan.FromSeconds(125), "  Hello there.\n How are   you? "));
        Assert.Equal("", LiveTranscriptText.FormatLine(TimeSpan.Zero, "   "));
        Assert.Equal("", LiveTranscriptText.FormatLine(TimeSpan.Zero, null));
    }

    [Fact]
    public void CountsWordsWithoutTimestamps()
    {
        var transcript = "[00:00] Hello there.\n[00:05] It's a test\n[1:00:01] of 10 words";
        Assert.Equal(8, LiveTranscriptText.CountWords(transcript));
        Assert.Equal(0, LiveTranscriptText.CountWords(""));
        Assert.Equal(0, LiveTranscriptText.CountWords(null));
        Assert.Equal("Hello there.\nIt's a test", LiveTranscriptText.StripTimestamps("[00:00] Hello there.\n[00:05] It's a test"));
    }

    [Fact]
    public void StripsOnlyLeadingTimestamps()
    {
        Assert.Equal("Meet at [10:30] today", LiveTranscriptText.StripTimestamps("[00:12] Meet at [10:30] today"));
    }

    [Fact]
    public void AutoSaveNameUsesTheSessionStart()
    {
        Assert.Equal("transcript_20261008_140305.md",
            LiveTranscriptText.AutoSaveFileName(new DateTime(2026, 10, 8, 14, 3, 5)));
    }

    [Fact]
    public void ExportNamesAndTypesFollowTheFormat()
    {
        var started = new DateTime(2026, 10, 8, 14, 3, 5);
        Assert.Equal("transcript_20261008_1403.md", LiveTranscriptText.ExportFileName(started));
        Assert.Equal("transcript_20261008_1403.txt", LiveTranscriptText.ExportFileName(started, markdown: false));
        Assert.Equal("text/markdown; charset=utf-8", LiveTranscriptText.ExportContentType(markdown: true));
        Assert.Equal("text/plain; charset=utf-8", LiveTranscriptText.ExportContentType(markdown: false));
    }

    [Theory]
    [InlineData(@"C:\notes\meeting.txt", false)]
    [InlineData("MEETING.TXT ", false)]
    [InlineData("meeting.md", true)]
    [InlineData("meeting", true)]
    [InlineData("meeting.markdown", true)]
    [InlineData(null, true)]
    public void SaveWritesMarkdownUnlessTheNameEndsInTxt(string? fileName, bool markdown)
    {
        Assert.Equal(markdown, LiveTranscriptText.IsMarkdownFileName(fileName));
    }

    [Fact]
    public void MarkdownDocumentHasTitleDateSummaryAndTranscript()
    {
        var doc = LiveTranscriptText.BuildDocument(
            "Live transcript",
            new DateTime(2026, 10, 8, 14, 3, 5),
            TimeSpan.FromSeconds(754),
            "[00:00] First line.\r\n\r\n[00:04] Second line.",
            "• Decide the date\n• Book the room",
            "Action items");

        Assert.StartsWith("# Live transcript\n\n_", doc);
        Assert.Contains("2026", doc);
        Assert.Contains("12:34 recorded", doc);
        Assert.Contains("## Action items\n\n• Decide the date\n\n• Book the room\n\n", doc);
        Assert.EndsWith("## Transcript\n\n[00:00] First line.\n\n[00:04] Second line.\n", doc);
        Assert.True(doc.IndexOf("## Action items", StringComparison.Ordinal) < doc.IndexOf("## Transcript", StringComparison.Ordinal));
    }

    [Fact]
    public void TextDocumentUsesUnderlinedHeadingsAndPlainLines()
    {
        var doc = LiveTranscriptText.BuildDocument(
            "Live transcript", new DateTime(2026, 10, 8), null, "[00:00] One.\n[00:03] Two.", "", markdown: false);

        Assert.StartsWith("Live transcript\n===============\n\n", doc);
        Assert.DoesNotContain("#", doc);
        Assert.DoesNotContain("Summary", doc);
        Assert.DoesNotContain("recorded", doc);
        Assert.EndsWith("Transcript\n----------\n\n[00:00] One.\n[00:03] Two.\n", doc);
    }

    [Fact]
    public void EmptyTranscriptIsMarked()
    {
        var doc = LiveTranscriptText.BuildDocument("T", DateTime.Now, null, "  ", null);
        Assert.EndsWith("## Transcript\n\n(empty)\n", doc);
    }

    [Fact]
    public void EverySummaryStyleHasItsOwnInstruction()
    {
        Assert.Equal(4, TranscriptSummaryStyles.Names.Count);
        var instructions = TranscriptSummaryStyles.Names.Select(TranscriptSummaryStyles.GetInstruction).ToList();
        Assert.Equal(instructions.Count, instructions.Distinct().Count());
        Assert.All(instructions, i => Assert.Contains("transcript", i, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("action items", "Action items")]
    [InlineData(" Meeting notes ", "Meeting notes")]
    [InlineData("nonsense", "Summary")]
    [InlineData(null, "Summary")]
    public void NormalizesStyleNames(string? name, string expected)
    {
        Assert.Equal(expected, TranscriptSummaryStyles.Normalize(name));
        Assert.Equal(TranscriptSummaryStyles.GetInstruction(expected), TranscriptSummaryStyles.GetInstruction(name));
    }

    [Theory]
    [InlineData("03:07", 0, 3, 7)]
    [InlineData("00:00", 0, 0, 0)]
    [InlineData("1:02:03", 1, 2, 3)]
    [InlineData(" 125:59 ", 2, 5, 59)]
    public void ReadsTimestamps(string text, int hours, int minutes, int seconds)
    {
        Assert.True(LiveTranscriptText.TryParseTimestamp(text, out var value));
        Assert.Equal(new TimeSpan(hours, minutes, seconds), value);
    }

    [Theory]
    [InlineData("3:7")]
    [InlineData("03:60")]
    [InlineData("1:02:61")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsOtherTimes(string? text)
    {
        Assert.False(LiveTranscriptText.TryParseTimestamp(text, out _));
    }

    [Fact]
    public void FindsTheFirstAndLastLineTimes()
    {
        var transcript = "typed\r\n[00:05] a\n  [12:30] b\n[03:00] c\nnot [09:00] at the start";

        Assert.True(LiveTranscriptText.TryReadLineTimestamp("[1:00:02] hello", out var at));
        Assert.Equal(new TimeSpan(1, 0, 2), at);
        Assert.False(LiveTranscriptText.TryReadLineTimestamp("hello [00:01]", out _));
        Assert.Equal(TimeSpan.FromSeconds(5), LiveTranscriptText.FirstTimestamp(transcript));
        Assert.Equal(new TimeSpan(0, 12, 30), LiveTranscriptText.LastTimestamp(transcript));
        Assert.Null(LiveTranscriptText.FirstTimestamp("no times"));
        Assert.Null(LiveTranscriptText.LastTimestamp(null));
    }
}
