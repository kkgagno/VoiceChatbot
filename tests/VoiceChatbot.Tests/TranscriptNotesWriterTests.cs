using VoiceChatbot;
using Xunit;

public class TranscriptNotesWriterTests
{
    /// <summary>Records every request and answers with a reply chosen by the test.</summary>
    private sealed class FakeModel
    {
        private readonly Func<TranscriptSummaryRequest, string> _reply;
        public List<TranscriptSummaryRequest> Requests { get; } = new();

        public FakeModel(Func<TranscriptSummaryRequest, string> reply) => _reply = reply;

        public Task<string> Summarize(TranscriptSummaryRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_reply(request));
        }
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Reports { get; } = new();
        public void Report(string value) => Reports.Add(value);
    }

    private static string Reply(TranscriptSummaryRequest r) => r.Kind switch
    {
        TranscriptSummaryKind.SectionNotes => "• point one\n• point two\n• point three",
        TranscriptSummaryKind.SummarySoFar => "Summary so far:\nThey are planning the launch.",
        _ => "Full summary of everything.",
    };

    // One line every 20 seconds from 00:00 for the given number of minutes.
    private static string Transcript(int minutes, int lineLength = 60)
    {
        var lines = new List<string>();
        for (var s = 0; s < minutes * 60; s += 20)
        {
            var prefix = $"[{LiveTranscriptText.FormatTimestamp(TimeSpan.FromSeconds(s))}] said ";
            lines.Add(prefix + new string('x', Math.Max(1, lineLength - prefix.Length)));
        }
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task UpdateAddsASectionForOnlyTheNewTextThenRewritesTheSummarySoFar()
    {
        var model = new FakeModel(Reply);
        var current =
            "SUMMARY SO FAR\nOld summary.\n\nNOTES BY TIME\n[00:00–05:12]\n- Edited by the user.";

        var result = await TranscriptNotesWriter.UpdateAsync(
            current, "[05:14] We moved the date.\n[07:40] Ana owns it.", TimeSpan.Parse("00:10:20"), "Key points", model.Summarize);

        Assert.True(result.SectionAdded);
        Assert.False(result.SummaryFailed);
        Assert.Equal(
            "SUMMARY SO FAR\nThey are planning the launch.\n\nNOTES BY TIME\n" +
            "[00:00–05:12]\n- Edited by the user.\n" +
            "[05:14–10:20]\n- point one\n- point two\n- point three",
            result.Notes);

        Assert.Equal(2, model.Requests.Count);
        var section = model.Requests[0];
        Assert.Equal(TranscriptSummaryKind.SectionNotes, section.Kind);
        Assert.True(section.IsLiveUpdate);
        Assert.Contains("[05:14] We moved the date.\n[07:40] Ana owns it.", section.UserMessage);
        Assert.DoesNotContain("Edited by the user", section.UserMessage);
        Assert.Contains("from 05:14 to 10:20", section.UserMessage);
        Assert.Contains("3 to 10 bullets", section.UserMessage);
        Assert.Contains("Never compress it into one sentence", section.UserMessage);

        var top = model.Requests[1];
        Assert.Equal(TranscriptSummaryKind.SummarySoFar, top.Kind);
        Assert.True(top.IsLiveUpdate);
        Assert.Equal(TranscriptSummaryStyles.KeyPoints, top.Style);
        Assert.Contains("[00:00–05:12]\n- Edited by the user.\n\n[05:14–10:20]\n- point one", top.UserMessage);
        Assert.DoesNotContain("We moved the date", top.UserMessage); // written from the notes, not the transcript
        Assert.Contains(TranscriptSummaryStyles.GetFormat("Key points"), top.UserMessage);
    }

    [Fact]
    public async Task FailedSummaryKeepsTheNewSectionAndTheOldTop()
    {
        var model = new FakeModel(r => r.Kind == TranscriptSummaryKind.SummarySoFar ? throw new HttpRequestException("down") : Reply(r));
        var current = "SUMMARY SO FAR\nOld summary.\n\nNOTES BY TIME\n[00:00–05:00]\n- a";

        var result = await TranscriptNotesWriter.UpdateAsync(current, "[05:01] b", TimeSpan.FromMinutes(10), null, model.Summarize);

        Assert.True(result.SectionAdded);
        Assert.True(result.SummaryFailed);
        Assert.IsType<HttpRequestException>(result.SummaryError);
        Assert.Equal(current + "\n[05:01–10:00]\n- point one\n- point two\n- point three", result.Notes);
    }

    [Fact]
    public async Task FailedOrEmptySectionNotesThrowSoTheCallerKeepsTheNotes()
    {
        var empty = new FakeModel(_ => "  ");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptNotesWriter.UpdateAsync("x", "[00:01] words", TimeSpan.FromMinutes(1), null, empty.Summarize));

        var failing = new FakeModel(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => TranscriptNotesWriter.UpdateAsync("x", "[00:01] words", TimeSpan.FromMinutes(1), null, failing.Summarize));
    }

    [Fact]
    public async Task UpdateWithoutNewTextChangesNothing()
    {
        var model = new FakeModel(Reply);
        var result = await TranscriptNotesWriter.UpdateAsync("notes", "  ", TimeSpan.FromMinutes(5), null, model.Summarize);
        Assert.Equal("notes", result.Notes);
        Assert.False(result.SectionAdded);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task FirstUpdateKeepsTextWithoutTheLayoutVerbatim()
    {
        var model = new FakeModel(Reply);
        var typed = "Things to ask Bob:\n1. budget\n2. dates";

        var first = await TranscriptNotesWriter.UpdateAsync(typed, "[00:03] Hello Bob.", TimeSpan.FromMinutes(5), null, model.Summarize);

        Assert.Equal(typed + "\n\nNOTES BY TIME\n[00:03–05:00]\n- point one\n- point two\n- point three", first.Notes);
        Assert.Single(model.Requests); // no summary yet: the typed text stays the top block

        // The next update rewrites the top and sends the typed text along so it is not lost.
        var second = await TranscriptNotesWriter.UpdateAsync(first.Notes, "[05:02] More.", TimeSpan.FromMinutes(10), null, model.Summarize);
        Assert.StartsWith("SUMMARY SO FAR\nThey are planning the launch.\n\nNOTES BY TIME\n[00:03–05:00]", second.Notes);
        Assert.Contains("EARLIER NOTES:\nThings to ask Bob:\n1. budget\n2. dates", model.Requests[^1].UserMessage);
    }

    [Fact]
    public async Task NewTextWithoutATimestampStartsWhereTheLastSectionEnded()
    {
        var model = new FakeModel(Reply);
        var result = await TranscriptNotesWriter.UpdateAsync(
            "NOTES BY TIME\n[00:00–04:30]\n- a", "typed words", TimeSpan.FromMinutes(9), null, model.Summarize);
        Assert.Contains("[04:30–09:00]", result.Notes);

        Assert.Equal("00:00–02:00", TranscriptNotesWriter.RangeFor("no stamp", TimeSpan.FromMinutes(2)));
        // A line later than the clock (e.g. pasted text) stretches the end.
        Assert.Equal("01:00–12:00", TranscriptNotesWriter.RangeFor("[01:00] a\n[12:00] b", TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task LongNewTextIsSentInPiecesIntoOneSection()
    {
        var piece = 0;
        var model = new FakeModel(r => r.Kind == TranscriptSummaryKind.SectionNotes ? $"- piece {++piece}" : "top");
        var text = Transcript(minutes: 30, lineLength: 400); // about 36,000 characters
        var pieces = TranscriptSummarizer.SplitIntoParts(text).Count;

        var result = await TranscriptNotesWriter.UpdateAsync("", text, TimeSpan.FromMinutes(30), null, model.Summarize);

        var parsed = TranscriptNotes.Parse(result.Notes);
        var section = Assert.Single(parsed.Sections);
        Assert.Equal("00:00–30:00", section.Label);
        Assert.True(pieces > 1);
        Assert.Equal(string.Join("\n", Enumerable.Range(1, pieces).Select(i => $"- piece {i}")), section.Body);
        Assert.Contains($"(PIECE 2 OF {pieces})", model.Requests[1].UserMessage);
        Assert.Equal(pieces + 1, model.Requests.Count);
    }

    [Fact]
    public async Task FinishAddsTheLastSectionThenAFullSummaryFromTheTranscript()
    {
        var model = new FakeModel(Reply);
        var transcript = "[00:00] Start.\n[05:01] Middle.\n[09:00] End.";
        var current = "SUMMARY SO FAR\nSo far.\n\nNOTES BY TIME\n[00:00–05:00]\n- a";
        var progress = new ListProgress();

        var result = await TranscriptNotesWriter.FinishAsync(
            current, "[05:01] Middle.\n[09:00] End.", transcript, TimeSpan.Parse("00:09:30"), "Action items", model.Summarize, progress);

        Assert.True(result.SectionAdded);
        Assert.Equal(
            "ACTION ITEMS\nFull summary of everything.\n\nNOTES BY TIME\n[00:00–05:00]\n- a\n" +
            "[05:01–09:30]\n- point one\n- point two\n- point three",
            result.Notes);

        var summary = model.Requests[^1];
        Assert.Equal(TranscriptSummaryKind.Summary, summary.Kind);
        Assert.True(summary.IsFinal);
        Assert.False(summary.IsLiveUpdate);
        Assert.Contains("TRANSCRIPT:\n" + transcript, summary.UserMessage);
        Assert.Equal(new[] { "Writing notes on 05:01–09:30...", "Writing the summary..." }, progress.Reports);
    }

    [Fact]
    public async Task FinishWithoutNewTextOnlyWritesTheSummary()
    {
        var model = new FakeModel(Reply);
        var current = "SUMMARY SO FAR\nSo far.\n\nNOTES BY TIME\n[00:00–05:00]\n- a";

        var result = await TranscriptNotesWriter.FinishAsync(current, "", "[00:00] Hi.", TimeSpan.FromMinutes(5), null, model.Summarize);

        Assert.False(result.SectionAdded);
        Assert.Equal("SUMMARY\nFull summary of everything.\n\nNOTES BY TIME\n[00:00–05:00]\n- a", result.Notes);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task FinishOfALongTranscriptCombinesTheSectionNotes()
    {
        var model = new FakeModel(Reply);
        var transcript = Transcript(minutes: 60, lineLength: 150); // about 27,000 characters
        var current = "SUMMARY SO FAR\nSo far.\n\nNOTES BY TIME\n[00:00–30:00]\n- first half\n[30:00–59:00]\n- second half";

        var result = await TranscriptNotesWriter.FinishAsync(current, "", transcript, TimeSpan.FromMinutes(60), null, model.Summarize);

        var combine = Assert.Single(model.Requests);
        Assert.Equal(TranscriptSummaryKind.Combine, combine.Kind);
        Assert.True(combine.IsFinal);
        Assert.Contains("[00:00–30:00]\n- first half", combine.UserMessage);
        Assert.DoesNotContain("said xxx", combine.UserMessage);
        Assert.StartsWith("SUMMARY\nFull summary of everything.", result.Notes);
    }

    [Fact]
    public async Task FinishKeepsTypedTextAndAFailedSummaryKeepsTheSection()
    {
        var model = new FakeModel(Reply);
        var typed = "My notes.";
        var kept = await TranscriptNotesWriter.FinishAsync(typed, "[00:00] Hi.", "[00:00] Hi.", TimeSpan.FromMinutes(1), null, model.Summarize);
        Assert.Equal("My notes.\n\nNOTES BY TIME\n[00:00–01:00]\n- point one\n- point two\n- point three", kept.Notes);
        Assert.Single(model.Requests);

        var failing = new FakeModel(r => r.Kind == TranscriptSummaryKind.Summary ? throw new TimeoutException() : Reply(r));
        var result = await TranscriptNotesWriter.FinishAsync(
            "NOTES BY TIME\n[00:00–01:00]\n- a", "[01:00] b", "[00:00] a\n[01:00] b", TimeSpan.FromMinutes(2), null, failing.Summarize);
        Assert.True(result.SummaryFailed);
        Assert.Equal("NOTES BY TIME\n[00:00–01:00]\n- a\n[01:00–02:00]\n- point one\n- point two\n- point three", result.Notes);
    }

    [Fact]
    public async Task RebuildWritesASectionPerWindowThenTheFullSummary()
    {
        var model = new FakeModel(Reply);
        var progress = new ListProgress();
        var transcript = Transcript(minutes: 12);

        var notes = await TranscriptNotesWriter.RebuildAsync(
            transcript, "Meeting notes", TimeSpan.FromMinutes(5), TimeSpan.Parse("00:12:30"), model.Summarize, progress);

        var parsed = TranscriptNotes.Parse(notes);
        Assert.Equal("MEETING NOTES", parsed.Heading);
        Assert.Equal("Full summary of everything.", parsed.Top);
        Assert.Equal(new[] { "00:00–05:00", "05:00–10:00", "10:00–12:30" }, parsed.Sections.Select(s => s.Label));
        Assert.All(parsed.Sections, s => Assert.Equal("- point one\n- point two\n- point three", s.Body));

        Assert.Equal(new[] { "Section 1 of 3...", "Section 2 of 3...", "Section 3 of 3...", "Writing the summary..." }, progress.Reports);
        Assert.Equal(4, model.Requests.Count);
        Assert.All(model.Requests.Take(3), r => Assert.False(r.IsFinal));
        Assert.Contains("[05:00] said", model.Requests[1].UserMessage);
        Assert.DoesNotContain("[04:40] said", model.Requests[1].UserMessage);
        Assert.Equal(TranscriptSummaryKind.Summary, model.Requests[^1].Kind);
        Assert.True(model.Requests[^1].IsFinal);
    }

    [Fact]
    public async Task RebuildCanBeCancelledBetweenSections()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeModel(r =>
        {
            cts.Cancel();
            return "- a";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranscriptNotesWriter.RebuildAsync(
            Transcript(12), null, TimeSpan.FromMinutes(5), null, model.Summarize, null, cts.Token));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task RebuildFillsInASectionTheModelLeftEmpty()
    {
        var calls = 0;
        var model = new FakeModel(r => r.Kind == TranscriptSummaryKind.SectionNotes ? (++calls == 2 ? " " : "- ok") : "top");
        var notes = await TranscriptNotesWriter.RebuildAsync(Transcript(12), null, TimeSpan.FromMinutes(5), null, model.Summarize);
        Assert.Equal(TranscriptNotesWriter.NoNotesBullet, TranscriptNotes.Parse(notes).Sections[1].Body);

        var emptyTop = new FakeModel(r => r.Kind == TranscriptSummaryKind.SectionNotes ? "- ok" : " ");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptNotesWriter.RebuildAsync(Transcript(3), null, TimeSpan.FromMinutes(5), null, emptyTop.Summarize));

        Assert.Equal("", await TranscriptNotesWriter.RebuildAsync("  ", null, TimeSpan.FromMinutes(5), null, model.Summarize));
    }

    [Fact]
    public void WindowsFollowTheLineTimes()
    {
        var transcript =
            "Typed before anything.\n" +
            "[00:10] a\n" +
            "untimed line\n" +
            "[04:59] b\n" +
            "\n" +
            "[15:02] c\n" +
            "[16:00] d";

        var windows = TranscriptNotesWriter.SplitIntoWindows(transcript, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(17));

        Assert.Equal(2, windows.Count);
        Assert.Equal("00:00–05:00", windows[0].Label);
        Assert.Equal("Typed before anything.\n[00:10] a\nuntimed line\n[04:59] b", windows[0].Text);
        Assert.Equal("15:00–17:00", windows[1].Label);
        Assert.Equal("[15:02] c\n[16:00] d", windows[1].Text);

        // Without a session length the last window ends at its last line.
        Assert.Equal("15:00–16:00", TranscriptNotesWriter.SplitIntoWindows(transcript, TimeSpan.FromMinutes(5))[1].Label);
        // Ten-minute windows.
        Assert.Equal(new[] { "00:00–10:00", "10:00–16:00" },
            TranscriptNotesWriter.SplitIntoWindows(transcript, TimeSpan.FromMinutes(10)).Select(w => w.Label));
    }

    [Fact]
    public void TranscriptWithoutTimesIsSplitIntoParts()
    {
        var text = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"line {i} " + new string('x', 90)));
        var windows = TranscriptNotesWriter.SplitIntoWindows(text, TimeSpan.FromMinutes(5));

        Assert.Equal(TranscriptSummarizer.SplitIntoParts(text).Count, windows.Count);
        Assert.True(windows.Count > 1);
        Assert.Equal(Enumerable.Range(1, windows.Count).Select(i => $"Part {i}"), windows.Select(w => w.Label));
        Assert.Equal(text, string.Join("\n", windows.Select(w => w.Text)));
        Assert.Empty(TranscriptNotesWriter.SplitIntoWindows("  ", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void HourLongSessionsUseHourTimes()
    {
        var windows = TranscriptNotesWriter.SplitIntoWindows("[58:00] a\n[1:01:30] b", TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { "55:00–1:00:00", "1:00:00–1:01:30" }, windows.Select(w => w.Label));
    }
}
