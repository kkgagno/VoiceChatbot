using VoiceChatbot;
using Xunit;

public class TranscriptSummaryPromptsTests
{
    [Fact]
    public void SummaryUsesTheStyleInstructionAndTheWholeTranscript()
    {
        var request = TranscriptSummaryPrompts.Summary("  [00:00] Hello.\n[00:04] Bye.  ", "key points");

        Assert.Equal(TranscriptSummaryKind.Summary, request.Kind);
        Assert.Equal(TranscriptSummaryStyles.KeyPoints, request.Style);
        Assert.True(request.IsFinal);
        Assert.Equal(
            TranscriptSummaryStyles.GetInstruction(TranscriptSummaryStyles.KeyPoints) + "\n\nTRANSCRIPT:\n[00:00] Hello.\n[00:04] Bye.",
            request.UserMessage);
    }

    [Fact]
    public void ComposeUserMessagePutsTheLabelAfterABlankLine()
    {
        Assert.Equal("Do this.\n\nTEXT:\nabc", TranscriptSummaryPrompts.ComposeUserMessage(" Do this. ", "TEXT", " abc \n"));
        Assert.Equal("Do this.\n\nTEXT:\n", TranscriptSummaryPrompts.ComposeUserMessage("Do this.", "TEXT", null));
    }

    [Fact]
    public void LiveNotesSendsCurrentNotesNewTextAndTheStyleFormat()
    {
        var request = TranscriptSummaryPrompts.LiveNotes(
            "• Budget is 5k\n• Ana owns the venue",
            "[02:10] We moved the date to May 3.",
            TranscriptSummaryStyles.MeetingNotes);

        Assert.Equal(TranscriptSummaryKind.LiveNotes, request.Kind);
        Assert.Equal(TranscriptSummaryStyles.MeetingNotes, request.Style);
        Assert.True(request.IsFinal);

        var message = request.UserMessage;
        Assert.Contains(TranscriptSummaryStyles.GetFormat(TranscriptSummaryStyles.MeetingNotes), message);
        Assert.Contains("full updated notes", message);
        Assert.Contains("keep every earlier point", message);
        Assert.Contains("merge the new information", message);
        Assert.Contains("no preamble", message);
        Assert.Contains("only what was said since the notes were last updated", message);
        Assert.Contains("CURRENT NOTES:\n• Budget is 5k\n• Ana owns the venue\n\nNEW TRANSCRIPT:\n[02:10] We moved the date to May 3.", message);
        Assert.EndsWith("May 3.", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void LiveNotesWithoutNotesSaysNoneYet(string? notes)
    {
        var message = TranscriptSummaryPrompts.LiveNotes(notes, "[00:00] Hi.", null).UserMessage;
        Assert.Contains("CURRENT NOTES:\n(none yet)\n\nNEW TRANSCRIPT:\n[00:00] Hi.", message);
        Assert.Contains(TranscriptSummaryStyles.GetFormat(TranscriptSummaryStyles.Summary), message);
    }

    [Fact]
    public void EveryStyleGetsADifferentLiveNotesPrompt()
    {
        var messages = TranscriptSummaryStyles.Names
            .Select(style => TranscriptSummaryPrompts.LiveNotes("notes", "new", style).UserMessage)
            .ToList();
        Assert.Equal(messages.Count, messages.Distinct().Count());
    }

    [Fact]
    public void PartPromptNamesThePartAndIsNotFinal()
    {
        var request = TranscriptSummaryPrompts.Part("[00:00] First words.", 2, 5, "action items");

        Assert.Equal(TranscriptSummaryKind.Part, request.Kind);
        Assert.False(request.IsFinal);
        Assert.Contains("part 2 of 5", request.UserMessage);
        Assert.Contains("combined into action items", request.UserMessage);
        Assert.EndsWith("TRANSCRIPT PART 2 OF 5:\n[00:00] First words.", request.UserMessage);
    }

    [Fact]
    public void CombinePromptListsThePartNotesInOrder()
    {
        var request = TranscriptSummaryPrompts.Combine(new[] { " first notes ", "second notes" }, TranscriptSummaryStyles.KeyPoints);

        Assert.Equal(TranscriptSummaryKind.Combine, request.Kind);
        Assert.True(request.IsFinal);
        Assert.Contains("notes on 2 consecutive parts", request.UserMessage);
        Assert.Contains(TranscriptSummaryStyles.GetFormat(TranscriptSummaryStyles.KeyPoints), request.UserMessage);
        Assert.EndsWith("PART NOTES:\nPart 1:\nfirst notes\n\nPart 2:\nsecond notes", request.UserMessage);
        Assert.False(TranscriptSummaryPrompts.Combine(new[] { "a", "b" }, null, isFinal: false).IsFinal);
    }

    [Theory]
    [InlineData("Here are the updated notes:\n• One\n• Two", "• One\n• Two")]
    [InlineData("Sure! Here is the summary:\n\nWe met.", "We met.")]
    [InlineData("Sure, here are the updated meeting notes:\r\nOverview\r\nWe met.", "Overview\nWe met.")]
    [InlineData("Updated notes:\n- A", "- A")]
    [InlineData("  • One\n• Two  ", "• One\n• Two")]
    [InlineData("Overview:\nWe met.", "Overview:\nWe met.")]
    [InlineData("Here is what was decided: the budget is 5k.", "Here is what was decided: the budget is 5k.")]
    [InlineData("Here are the updated notes:", "Here are the updated notes:")]
    [InlineData(null, "")]
    public void StripsOnlyALeadInLine(string? reply, string expected)
    {
        Assert.Equal(expected, TranscriptSummaryPrompts.StripPreamble(reply));
    }

    [Fact]
    public void EveryStyleHasItsOwnFormat()
    {
        var formats = TranscriptSummaryStyles.Names.Select(TranscriptSummaryStyles.GetFormat).ToList();
        Assert.Equal(formats.Count, formats.Distinct().Count());
        Assert.Equal(TranscriptSummaryStyles.GetFormat(TranscriptSummaryStyles.Summary), TranscriptSummaryStyles.GetFormat("unknown"));
    }
}
