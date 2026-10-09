using VoiceChatbot;
using Xunit;

public class TranscriptNotesTests
{
    private const string Layout =
        "SUMMARY SO FAR\n" +
        "They planned the launch.\n" +
        "\n" +
        "NOTES BY TIME\n" +
        "[00:00–05:12]\n" +
        "- Launch moved to May 3.\n" +
        "- Ana owns the press release.\n" +
        "[05:12–10:20]\n" +
        "- Budget is 5,000 dollars.";

    [Fact]
    public void ParsesAndRendersTheLayout()
    {
        var notes = TranscriptNotes.Parse(Layout);

        Assert.True(notes.IsStructured);
        Assert.Equal(TranscriptNotes.SummarySoFarHeading, notes.Heading);
        Assert.Equal("They planned the launch.", notes.Top);
        Assert.Equal(2, notes.Sections.Count);
        Assert.Equal("00:00–05:12", notes.Sections[0].Label);
        Assert.Equal("- Launch moved to May 3.\n- Ana owns the press release.", notes.Sections[0].Body);
        Assert.Equal("05:12–10:20", notes.Sections[1].Label);
        Assert.True(notes.HasSections);
        Assert.False(notes.TopIsUserText);
        Assert.Equal(Layout, notes.Render());
    }

    [Fact]
    public void ToleratesEditsAndWindowsLineEndings()
    {
        var edited =
            "  KEY POINTS \r\n" +
            "- First.\r\n\r\n" +
            "notes by time:\r\n" +
            "My own line before any section.\r\n" +
            "[ 00:00 - 05:12 ]\r\n" +
            "- Launch.\r\n" +
            "\r\n" +
            "Something I added.\r\n" +
            "[part 2]\r\n" +
            "- More.\r\n";

        var notes = TranscriptNotes.Parse(edited);

        Assert.Equal("KEY POINTS", notes.Heading);
        Assert.Equal("- First.", notes.Top);
        Assert.Equal(3, notes.Sections.Count);
        Assert.Equal("", notes.Sections[0].Label);
        Assert.Equal("My own line before any section.", notes.Sections[0].Body);
        Assert.Equal("00:00–05:12", notes.Sections[1].Label);
        Assert.Equal("- Launch.\n\nSomething I added.", notes.Sections[1].Body);
        Assert.Equal("Part 2", notes.Sections[2].Label);

        // Rendering and parsing again keeps every line.
        var again = TranscriptNotes.Parse(notes.Render());
        Assert.Equal(notes.Render(), again.Render());
        Assert.Contains("Something I added.", again.Render());
        Assert.Contains("My own line before any section.", again.Render());
    }

    [Fact]
    public void TextWithoutTheLayoutIsKeptVerbatim()
    {
        var text = "Meeting with Bob.\n\n[00:00] not a header because it has text\nBudget: 5k";
        var notes = TranscriptNotes.Parse(text);

        Assert.False(notes.IsStructured);
        Assert.Null(notes.Heading);
        Assert.Equal(text, notes.Top);
        Assert.True(notes.TopIsUserText);
        Assert.False(notes.HasSections);
        Assert.Equal(text, notes.Render());

        var withSection = notes.AddSection(new TranscriptNotesSection("00:00–05:00", "- Hello."));
        Assert.Equal(text + "\n\nNOTES BY TIME\n[00:00–05:00]\n- Hello.", withSection.Render());
        Assert.True(TranscriptNotes.Parse(withSection.Render()).TopIsUserText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void EmptyTextIsEmptyNotes(string? text)
    {
        var notes = TranscriptNotes.Parse(text);
        Assert.True(notes.IsEmpty);
        Assert.True(notes.IsStructured);
        Assert.Equal("", notes.Render());
    }

    [Fact]
    public void AddingASectionKeepsEarlierSectionsAndWithTopReplacesOnlyTheTop()
    {
        var notes = TranscriptNotes.Parse(Layout)
            .AddSection(new TranscriptNotesSection("10:20–15:00", "- Wrap up."))
            .WithTop("ACTION ITEMS", "- Ana: press release by May 1");

        Assert.Equal(
            "ACTION ITEMS\n- Ana: press release by May 1\n\nNOTES BY TIME\n" +
            "[00:00–05:12]\n- Launch moved to May 3.\n- Ana owns the press release.\n" +
            "[05:12–10:20]\n- Budget is 5,000 dollars.\n" +
            "[10:20–15:00]\n- Wrap up.",
            notes.Render());
        Assert.True(notes.HasFullSummary("Action items"));
        Assert.False(notes.HasFullSummary("Summary"));
        Assert.False(TranscriptNotes.Parse(Layout).HasFullSummary("Summary"));
    }

    [Fact]
    public void SectionsWithoutATopStartWithNotesByTime()
    {
        var notes = new TranscriptNotes(null, null, new[] { new TranscriptNotesSection("00:00–05:00", "- Hi.") });
        Assert.Equal("NOTES BY TIME\n[00:00–05:00]\n- Hi.", notes.Render());
        Assert.Equal(new[] { "[00:00–05:00]\n- Hi." }, notes.SectionTexts());
    }

    [Fact]
    public void AHeadingWithoutSectionsIsStillRecognized()
    {
        var notes = TranscriptNotes.Parse("SUMMARY\nAll done.");
        Assert.True(notes.IsStructured);
        Assert.Equal("SUMMARY", notes.Heading);
        Assert.Equal("All done.", notes.Top);
        Assert.True(notes.HasFullSummary("summary"));
        Assert.Equal("SUMMARY\nAll done.", notes.Render());
    }

    [Theory]
    [InlineData("Meeting notes:\n- Attendees: Bob, Alice\n- Agenda: budget")]
    [InlineData("Meeting Notes\n- Attendees: Bob, Alice")]
    [InlineData("Summary\nWe talked.")]
    [InlineData("action items:\n- Bob: slides")]
    public void ALineThatOnlyLooksLikeAHeadingIsUserText(string text)
    {
        var notes = TranscriptNotes.Parse(text);
        Assert.False(notes.IsStructured);
        Assert.Null(notes.Heading);
        Assert.Equal(text, notes.Top);
        Assert.True(notes.TopIsUserText);

        // After the first section it is still the user's text, not a summary to replace.
        var withSection = TranscriptNotes.Parse(notes.AddSection(new TranscriptNotesSection("00:00–05:00", "- Hi.")).Render());
        Assert.True(withSection.IsStructured);
        Assert.Null(withSection.Heading);
        Assert.Equal(text, withSection.Top);
        Assert.True(withSection.TopIsUserText);
    }

    [Fact]
    public void FormatsAndReadsRanges()
    {
        Assert.Equal("00:00–05:12", TranscriptNotes.FormatRange(TimeSpan.Zero, new TimeSpan(0, 5, 12)));
        Assert.Equal("59:30–1:04:05", TranscriptNotes.FormatRange(new TimeSpan(0, 59, 30), new TimeSpan(1, 4, 5)));
        Assert.Equal("03:00–03:00", TranscriptNotes.FormatRange(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1)));

        Assert.True(TranscriptNotes.TryParseRange("59:30–1:04:05", out var start, out var end));
        Assert.Equal(new TimeSpan(0, 59, 30), start);
        Assert.Equal(new TimeSpan(1, 4, 5), end);
        Assert.False(TranscriptNotes.TryParseRange("Part 2", out _, out _));
        Assert.Equal("Part 2", TranscriptNotes.PartLabel(2));

        Assert.True(TranscriptNotes.TryReadHeader("[05:12—10:20]", out var label));
        Assert.Equal("05:12–10:20", label);
        Assert.False(TranscriptNotes.TryReadHeader("[05:12] said something", out _));
        Assert.False(TranscriptNotes.TryReadHeader("- [05:12–10:20]", out _));
    }

    [Fact]
    public void CleansSectionReplies()
    {
        var reply = "Here are the notes:\n• Budget is 5k\n\n* Ana owns it\n[00:00–05:00]\nNOTES BY TIME\n  – nested point\n- already fine";
        Assert.Equal("- Budget is 5k\n- Ana owns it\n  - nested point\n- already fine", TranscriptNotes.CleanSectionBody(reply));
        Assert.Equal("", TranscriptNotes.CleanSectionBody("   "));
    }

    [Fact]
    public void CleansTopReplies()
    {
        Assert.Equal("They agreed.\n\n- Ana: slides", TranscriptNotes.CleanTop("Summary so far:\nThey agreed.\n\n\n\n- Ana: slides\nNOTES BY TIME"));
        Assert.Equal("Overview\nThey met.", TranscriptNotes.CleanTop("Sure! Here is the summary:\nOverview\nThey met."));
    }

    [Fact]
    public void TheSavedDocumentHeadingIsNotesForTheLayout()
    {
        Assert.Equal("Notes", TranscriptNotes.DocumentHeading(Layout, "Key points"));
        Assert.Equal("Key points", TranscriptNotes.DocumentHeading("my notes", "key points"));
        Assert.Equal("Summary", TranscriptNotes.DocumentHeading("", null));
    }
}
