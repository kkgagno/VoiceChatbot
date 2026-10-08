using VoiceChatbot;
using Xunit;

public class TranscriptSummarizerTests
{
    private static string Transcript(int lines, int lineLength = 100)
    {
        var all = new List<string>();
        for (var i = 0; i < lines; i++)
        {
            var prefix = $"[{i / 60:00}:{i % 60:00}] line {i} ";
            all.Add(prefix + new string('x', Math.Max(0, lineLength - prefix.Length)));
        }
        return string.Join("\n", all);
    }

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

    [Fact]
    public void SplittingStartsAboveTheSinglePassLimit()
    {
        Assert.False(TranscriptSummarizer.NeedsSplitting(new string('a', TranscriptSummarizer.SinglePassLimit)));
        Assert.True(TranscriptSummarizer.NeedsSplitting(new string('a', TranscriptSummarizer.SinglePassLimit + 1)));
        Assert.False(TranscriptSummarizer.NeedsSplitting(null));
        Assert.Equal(24_000, TranscriptSummarizer.SinglePassLimit);
        Assert.Equal(12_000, TranscriptSummarizer.PartLength);
    }

    [Fact]
    public void SplitsOnLineBoundariesWithoutLosingText()
    {
        var transcript = Transcript(300); // about 30,000 characters
        var parts = TranscriptSummarizer.SplitIntoParts(transcript);

        Assert.Equal(3, parts.Count);
        Assert.All(parts, p => Assert.True(p.Length <= TranscriptSummarizer.PartLength));
        Assert.All(parts, p => Assert.StartsWith("[", p));
        Assert.Equal(transcript, string.Join("\n", parts));
    }

    [Fact]
    public void SplittingDropsBlankLinesAndNormalizesLineEndings()
    {
        var parts = TranscriptSummarizer.SplitIntoParts("[00:00] one\r\n\r\n  \r\n[00:05] two  \r\n[00:09] three", partLength: 25);
        Assert.Equal(new[] { "[00:00] one\n[00:05] two", "[00:09] three" }, parts);
        Assert.Empty(TranscriptSummarizer.SplitIntoParts("  \n "));
        Assert.Empty(TranscriptSummarizer.SplitIntoParts(null));
    }

    [Fact]
    public void ALineLongerThanAPartIsCutBetweenWords()
    {
        var line = string.Join(" ", Enumerable.Repeat("word", 50)); // 249 characters
        var parts = TranscriptSummarizer.SplitIntoParts(line, partLength: 60);

        Assert.True(parts.Count >= 5);
        Assert.All(parts, p => Assert.True(p.Length <= 60));
        Assert.All(parts, p => Assert.DoesNotContain("wor ", p + " ")); // no word cut in half
        Assert.Equal(line, string.Join(" ", parts.SelectMany(p => p.Split('\n'))));
    }

    [Fact]
    public void AWordLongerThanAPartIsCutHard()
    {
        var parts = TranscriptSummarizer.SplitIntoParts(new string('z', 25), partLength: 10);
        Assert.Equal(new[] { new string('z', 10), new string('z', 10), new string('z', 5) }, parts);
    }

    [Fact]
    public void GroupsForCombiningHaveAtLeastTwoNotes()
    {
        var notes = Enumerable.Range(1, 7).Select(i => new string((char)('a' + i), 5000)).ToList();
        var groups = TranscriptSummarizer.GroupForCombining(notes, maxChars: 12_000);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.True(g.Count >= 2));
        Assert.Equal(notes, groups.SelectMany(g => g));

        var single = TranscriptSummarizer.GroupForCombining(new[] { "one" });
        Assert.Single(single);
    }

    [Fact]
    public async Task ShortTranscriptIsSummarizedInOneRequest()
    {
        var model = new FakeModel(_ => "Here is the summary:\nThey agreed.");
        var result = await TranscriptSummarizer.SummarizeAsync("[00:00] We agree.", "summary", model.Summarize);

        Assert.Equal("They agreed.", result);
        var request = Assert.Single(model.Requests);
        Assert.Equal(TranscriptSummaryKind.Summary, request.Kind);
        Assert.True(request.IsFinal);
        Assert.Contains("[00:00] We agree.", request.UserMessage);
    }

    [Fact]
    public async Task EmptyTranscriptMakesNoRequest()
    {
        var model = new FakeModel(_ => "x");
        Assert.Equal("", await TranscriptSummarizer.SummarizeAsync("  ", null, model.Summarize));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task LongTranscriptIsSummarizedInPartsThenCombined()
    {
        var transcript = Transcript(300);
        var progress = new ListProgress();
        var model = new FakeModel(r => r.Kind == TranscriptSummaryKind.Part
            ? $"notes {r.UserMessage.Split("TRANSCRIPT PART ")[1][..1]}"
            : "combined action items");

        var result = await TranscriptSummarizer.SummarizeAsync(transcript, "Action items", model.Summarize, progress);

        Assert.Equal("combined action items", result);
        Assert.Equal(4, model.Requests.Count);
        Assert.All(model.Requests.Take(3), r => Assert.Equal(TranscriptSummaryKind.Part, r.Kind));
        Assert.All(model.Requests.Take(3), r => Assert.False(r.IsFinal));
        Assert.All(model.Requests, r => Assert.Equal(TranscriptSummaryStyles.ActionItems, r.Style));

        var combine = model.Requests[^1];
        Assert.Equal(TranscriptSummaryKind.Combine, combine.Kind);
        Assert.True(combine.IsFinal);
        Assert.EndsWith("Part 1:\nnotes 1\n\nPart 2:\nnotes 2\n\nPart 3:\nnotes 3", combine.UserMessage);

        Assert.Equal(new[]
        {
            "Summarizing part 1 of 3...",
            "Summarizing part 2 of 3...",
            "Summarizing part 3 of 3...",
            "Combining the notes from 3 parts...",
        }, progress.Reports);
    }

    [Fact]
    public async Task EmptyPartNotesAreLeftOut()
    {
        var calls = 0;
        var model = new FakeModel(r => r.Kind == TranscriptSummaryKind.Part ? (++calls == 2 ? "  " : $"notes {calls}") : "done");

        await TranscriptSummarizer.SummarizeAsync(Transcript(300), null, model.Summarize);

        var combine = model.Requests[^1];
        Assert.Contains("notes on 2 consecutive parts", combine.UserMessage);
        Assert.EndsWith("Part 1:\nnotes 1\n\nPart 2:\nnotes 3", combine.UserMessage);
    }

    [Fact]
    public async Task VeryLongNotesAreCombinedInGroupsFirst()
    {
        var transcript = Transcript(1200); // about 120,000 characters: 11 parts
        var model = new FakeModel(r => r.Kind switch
        {
            TranscriptSummaryKind.Part => new string('n', 5000),
            TranscriptSummaryKind.Combine when !r.IsFinal => new string('g', 3000),
            _ => "final",
        });

        var result = await TranscriptSummarizer.SummarizeAsync(transcript, null, model.Summarize);

        Assert.Equal("final", result);
        var parts = model.Requests.Count(r => r.Kind == TranscriptSummaryKind.Part);
        var groups = model.Requests.Count(r => r.Kind == TranscriptSummaryKind.Combine && !r.IsFinal);
        Assert.Equal(TranscriptSummarizer.SplitIntoParts(transcript).Count, parts);
        Assert.True(parts > 8);
        Assert.True(groups >= 2);
        Assert.Single(model.Requests, r => r.IsFinal);
        Assert.True(model.Requests[^1].UserMessage.Length < 30_000);
    }

    [Fact]
    public async Task CancellationStopsBetweenParts()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeModel(r =>
        {
            cts.Cancel();
            return "notes";
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TranscriptSummarizer.SummarizeAsync(Transcript(300), null, model.Summarize, null, cts.Token));
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task LiveUpdateSendsTheNotesAndOnlyTheNewText()
    {
        var model = new FakeModel(_ => "Updated notes:\n• Budget is 5k\n• Date moved to May 3");

        var result = await TranscriptSummarizer.UpdateNotesAsync(
            "• Budget is 5k", "[02:10] We moved the date to May 3.", TranscriptSummaryStyles.KeyPoints, model.Summarize);

        Assert.Equal("• Budget is 5k\n• Date moved to May 3", result);
        var request = Assert.Single(model.Requests);
        Assert.Equal(TranscriptSummaryKind.LiveNotes, request.Kind);
        Assert.True(request.IsFinal);
        Assert.Contains("CURRENT NOTES:\n• Budget is 5k\n\nNEW TRANSCRIPT:\n[02:10] We moved the date to May 3.", request.UserMessage);
    }

    [Fact]
    public async Task LiveUpdateWithoutNewTextKeepsTheNotes()
    {
        var model = new FakeModel(_ => "changed");
        Assert.Equal("notes", await TranscriptSummarizer.UpdateNotesAsync(" notes ", "  ", null, model.Summarize));
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task LiveUpdateMergesLongNewTextOnePartAtATime()
    {
        var step = 0;
        var model = new FakeModel(_ => $"notes after step {++step}");

        var result = await TranscriptSummarizer.UpdateNotesAsync("start", Transcript(300), null, model.Summarize);

        Assert.Equal("notes after step 3", result);
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains("CURRENT NOTES:\nstart\n", model.Requests[0].UserMessage);
        Assert.Contains("CURRENT NOTES:\nnotes after step 1\n", model.Requests[1].UserMessage);
        Assert.Contains("CURRENT NOTES:\nnotes after step 2\n", model.Requests[2].UserMessage);
        Assert.Equal(new[] { false, false, true }, model.Requests.Select(r => r.IsFinal));
    }

    [Fact]
    public async Task EmptyLiveUpdateReplyThrowsSoTheOldNotesStay()
    {
        var model = new FakeModel(_ => "   ");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptSummarizer.UpdateNotesAsync("old notes", "[00:01] new words", null, model.Summarize));
    }
}
