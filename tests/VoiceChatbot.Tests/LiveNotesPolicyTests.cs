using System.Globalization;
using VoiceChatbot;
using Xunit;

public class LiveNotesPolicyTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 14, 0, 0, DateTimeKind.Utc);

    private static string Words(int count, string start = "[00:00]") =>
        start + " " + string.Join(" ", Enumerable.Range(1, count).Select(i => $"w{i}"));

    private static LiveNotesPolicy Policy(int minutes = 2)
    {
        var policy = new LiveNotesPolicy { Enabled = true, Interval = TimeSpan.FromMinutes(minutes) };
        policy.RestartClock(T0);
        return policy;
    }

    [Fact]
    public void DueOnlyWhenEnabledRecordingIntervalPassedAndEnoughNewWords()
    {
        var policy = Policy();
        var transcript = Words(45);
        var later = T0.AddMinutes(2);

        Assert.Equal(LiveNotesCheck.Due, policy.Check(later, recording: true, summaryRunning: false, transcript));
        Assert.Equal(LiveNotesCheck.NotRecording, policy.Check(later, recording: false, summaryRunning: false, transcript));
        Assert.Equal(LiveNotesCheck.SummaryRunning, policy.Check(later, recording: true, summaryRunning: true, transcript));
        Assert.Equal(LiveNotesCheck.WaitingForInterval, policy.Check(T0.AddSeconds(105), true, false, transcript));
        Assert.Equal(LiveNotesCheck.TooFewNewWords, policy.Check(later, true, false, Words(39)));
        Assert.Equal(LiveNotesCheck.Due, policy.Check(later, true, false, Words(40)));

        policy.Enabled = false;
        Assert.Equal(LiveNotesCheck.Disabled, policy.Check(later, true, false, transcript));
    }

    [Fact]
    public void TimestampsAreNotCountedAsWords()
    {
        var policy = Policy();
        var transcript = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"[00:{i:00}] hello"));
        Assert.Equal(30, policy.CountNewWords(transcript));
        Assert.Equal(LiveNotesCheck.TooFewNewWords, policy.Check(T0.AddMinutes(3), true, false, transcript));
    }

    [Fact]
    public void ARunningUpdateIsNotStartedTwice()
    {
        var policy = Policy();
        var ticket = policy.TryBegin(T0.AddMinutes(2), true, false, Words(50));

        Assert.NotNull(ticket);
        Assert.True(policy.IsRunning);
        Assert.False(ticket!.IsFinal);
        Assert.Equal(50, ticket.NewWords);
        Assert.Equal(LiveNotesCheck.AlreadyRunning, policy.Check(T0.AddMinutes(3), true, false, Words(80)));
        Assert.Null(policy.TryBegin(T0.AddMinutes(3), true, false, Words(80)));
    }

    [Fact]
    public void CompletedUpdateSendsOnlyTextAddedSinceThen()
    {
        var policy = Policy();
        var first = Words(50);
        var ticket = policy.TryBegin(T0.AddMinutes(2), true, false, first)!;
        Assert.Equal(first, ticket.NewText);

        // Lines keep arriving while the model writes the notes.
        var grown = first + "\n" + Words(10, "[02:05]");
        Assert.True(policy.Complete(ticket, T0.AddMinutes(2.5)));
        Assert.False(policy.IsRunning);
        Assert.Equal(first, policy.ProcessedTranscript);
        Assert.Equal(Words(10, "[02:05]"), policy.GetNewText(grown));

        // The interval starts again when the update finished, and 40 new words are needed.
        Assert.Equal(LiveNotesCheck.WaitingForInterval, policy.Check(T0.AddMinutes(4), true, false, grown));
        Assert.Equal(LiveNotesCheck.TooFewNewWords, policy.Check(T0.AddMinutes(4.5), true, false, grown));

        var more = grown + "\n" + Words(40, "[03:00]");
        var next = policy.TryBegin(T0.AddMinutes(4.5), true, false, more)!;
        Assert.Equal(Words(10, "[02:05]") + "\n" + Words(40, "[03:00]"), next.NewText);
        Assert.Equal(50, next.NewWords);
    }

    [Fact]
    public void FailedUpdateKeepsTheOldTextAndWaitsAFullInterval()
    {
        var policy = Policy();
        var transcript = Words(50);
        var ticket = policy.TryBegin(T0.AddMinutes(2), true, false, transcript)!;

        policy.Fail(ticket, T0.AddMinutes(2.2));

        Assert.False(policy.IsRunning);
        Assert.Equal("", policy.ProcessedTranscript);
        Assert.Equal(transcript, policy.GetNewText(transcript));
        Assert.Equal(LiveNotesCheck.WaitingForInterval, policy.Check(T0.AddMinutes(4), true, false, transcript));
        Assert.Equal(LiveNotesCheck.Due, policy.Check(T0.AddMinutes(4.2), true, false, transcript));
    }

    [Fact]
    public void AbandonedUpdateCanBeRetriedOnTheNextTick()
    {
        var policy = Policy();
        var transcript = Words(50);
        var ticket = policy.TryBegin(T0.AddMinutes(2), true, false, transcript)!;

        policy.Abandon(ticket);

        Assert.False(policy.IsRunning);
        Assert.Equal(LiveNotesCheck.Due, policy.Check(T0.AddMinutes(2.25), true, false, transcript));
    }

    [Fact]
    public void ResetDropsTheUpdateInProgressAndTheCoveredText()
    {
        var policy = Policy();
        var ticket = policy.TryBegin(T0.AddMinutes(2), true, false, Words(50))!;
        Assert.True(policy.IsCurrent(ticket));

        policy.Reset(T0.AddMinutes(3));

        Assert.False(policy.IsCurrent(ticket));
        Assert.False(policy.IsRunning);
        Assert.False(policy.Complete(ticket, T0.AddMinutes(3.1)));
        Assert.Equal("", policy.ProcessedTranscript);
        Assert.Equal(LiveNotesCheck.WaitingForInterval, policy.Check(T0.AddMinutes(4), true, false, Words(50)));

        // A late Fail or Abandon of the old ticket does not touch a newer update.
        var fresh = policy.TryBegin(T0.AddMinutes(5), true, false, Words(50))!;
        policy.Fail(ticket, T0.AddMinutes(5));
        policy.Abandon(ticket);
        Assert.True(policy.IsCurrent(fresh));
    }

    [Fact]
    public void ManualSummaryCoversTheTranscriptSoFar()
    {
        var policy = Policy();
        var transcript = Words(100);

        policy.MarkSummarized(transcript, T0.AddMinutes(1));

        Assert.Equal(0, policy.CountNewWords(transcript));
        Assert.Equal(LiveNotesCheck.WaitingForInterval, policy.Check(T0.AddMinutes(2), true, false, transcript + "\n" + Words(50, "[01:30]")));
        Assert.Equal(Words(50, "[01:30]"), policy.GetNewText(transcript + "\n" + Words(50, "[01:30]")));
    }

    [Fact]
    public void EditedTranscriptFallsBackToTheLineWhereTheNotesEnded()
    {
        var policy = Policy();
        var covered = "[00:00] Meet Jon tomorrow.\n[00:05] Budget is five.";
        policy.MarkSummarized(covered, T0);

        // The user fixed a name while stopped, then recording went on.
        var edited = "[00:00] Meet John tomorrow.\n[00:05] Budget is five.\n[00:09] New point.";
        Assert.Equal("[00:05] Budget is five.\n[00:09] New point.", policy.GetNewText(edited));

        // Shorter than the covered text: only the last line is new.
        Assert.Equal("[00:00] Short.", policy.GetNewText("[00:00] Short."));
    }

    [Fact]
    public void FinalUpdateNeedsOnlyOneNewWord()
    {
        var policy = Policy();
        var transcript = Words(50);
        policy.MarkSummarized(transcript, T0);

        Assert.Equal(LiveNotesCheck.TooFewNewWords, policy.CheckFinal(false, transcript));
        Assert.Null(policy.TryBeginFinal(false, transcript));

        var grown = transcript + "\n[01:00] Bye.";
        Assert.Equal(LiveNotesCheck.SummaryRunning, policy.CheckFinal(true, grown));
        var ticket = policy.TryBeginFinal(false, grown);
        Assert.NotNull(ticket);
        Assert.True(ticket!.IsFinal);
        Assert.Equal("[01:00] Bye.", ticket.NewText);
        Assert.Equal(LiveNotesCheck.AlreadyRunning, policy.CheckFinal(false, grown));

        policy.Enabled = false;
        Assert.Equal(LiveNotesCheck.Disabled, policy.CheckFinal(false, grown));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(8, 10)]
    [InlineData(60, 10)]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    public void NormalizesTheInterval(int minutes, int expected)
    {
        Assert.Equal(expected, LiveNotesPolicy.NormalizeIntervalMinutes(minutes));
    }

    [Fact]
    public void DefaultsMatchTheSettings()
    {
        var policy = new LiveNotesPolicy();
        Assert.False(policy.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Interval);
        Assert.Equal(40, policy.MinNewWords);
        Assert.Equal(TimeSpan.FromSeconds(15), LiveNotesPolicy.CheckEvery);
        Assert.Equal(new[] { 2, 5, 10 }, LiveNotesPolicy.IntervalChoicesMinutes);
    }

    [Fact]
    public void FormatsTheUpdateTime()
    {
        Assert.Equal("Notes updated 2:41 PM",
            LiveNotesPolicy.FormatUpdated(new DateTime(2026, 10, 8, 14, 41, 9), CultureInfo.InvariantCulture));
        Assert.Equal("Notes updated 9:05 AM",
            LiveNotesPolicy.FormatUpdated(new DateTime(2026, 10, 8, 9, 5, 0), CultureInfo.InvariantCulture));

        var noAmPm = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        noAmPm.DateTimeFormat.AMDesignator = "";
        noAmPm.DateTimeFormat.PMDesignator = "";
        Assert.Equal("Notes updated 14:41", LiveNotesPolicy.FormatUpdated(new DateTime(2026, 10, 8, 14, 41, 0), noAmPm));
    }
}
