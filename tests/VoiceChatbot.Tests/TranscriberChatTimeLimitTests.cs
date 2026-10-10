using VoiceChatbot;
using Xunit;

public class TranscriberChatTimeLimitTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    private static TranscriptSummaryRequest Request() => new(TranscriptSummaryKind.SectionNotes, "Summary", "notes on this");

    [Fact]
    public void EachRequestMayTakeFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TranscriberChatTimeLimit.Limit);
        Assert.Equal("The chat model on the PC did not answer within 5 minutes.",
            TranscriberChatTimeLimit.TimedOutMessage(TranscriberChatTimeLimit.Limit));
        Assert.Equal("The chat model on the PC did not answer within 1 minute.",
            TranscriberChatTimeLimit.TimedOutMessage(TimeSpan.FromMinutes(1)));
        Assert.Equal("The chat model on the PC did not answer within 1 second.",
            TranscriberChatTimeLimit.TimedOutMessage(Short));
    }

    [Fact]
    public async Task AnAnswerInTimeIsPassedOn()
    {
        CancellationToken seen = default;
        var summarize = TranscriberChatTimeLimit.Apply((_, ct) =>
        {
            seen = ct;
            return Task.FromResult("- the notes");
        });

        Assert.Equal("- the notes", await summarize(Request(), CancellationToken.None));
        Assert.True(seen.CanBeCanceled);
    }

    [Fact]
    public async Task ASlowRequestIsCancelledAndFailsWithTheMessage()
    {
        CancellationToken seen = default;
        var summarize = TranscriberChatTimeLimit.Apply(async (_, ct) =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, Short);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => summarize(Request(), CancellationToken.None));

        Assert.Equal(TranscriberChatTimeLimit.TimedOutMessage(Short), ex.Message);
        Assert.True(seen.IsCancellationRequested); // the HTTP request gets the cancel
    }

    [Fact]
    public async Task ARequestThatIgnoresItsTokenStillEndsOnTime()
    {
        var never = new TaskCompletionSource<string>();
        var summarize = TranscriberChatTimeLimit.Apply((_, _) => never.Task, Short);

        var run = summarize(Request(), CancellationToken.None);
        var first = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(run, first);
        await Assert.ThrowsAsync<TimeoutException>(() => run);
    }

    [Fact]
    public async Task CancellingTheJobIsACancelNotATimeout()
    {
        using var job = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var summarize = TranscriberChatTimeLimit.Apply(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, TimeSpan.FromMinutes(5));

        var run = summarize(Request(), job.Token);
        await started.Task;
        job.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task OtherErrorsArePassedOn()
    {
        var summarize = TranscriberChatTimeLimit.Apply(
            (_, _) => Task.FromException<string>(new InvalidOperationException("Select a model first.")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => summarize(Request(), CancellationToken.None));
        Assert.Equal("Select a model first.", ex.Message);
    }

    [Fact]
    public void ALimitMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriberChatTimeLimit.Apply((_, _) => Task.FromResult(""), TimeSpan.Zero));
    }

    [Fact]
    public async Task ASummarizeJobWhoseChatModelStallsFailsWithTheMessageAndFreesItsSlot()
    {
        var jobs = new TranscriberJobs(maxRunning: 1);
        var summarize = TranscriberChatTimeLimit.Apply(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, Short);

        var job = jobs.TryStart("Summary", (progress, ct) =>
            TranscriptNotesWriter.RebuildAsync("[00:00] Hello there.\n[00:20] Next point.", "Summary",
                TimeSpan.FromMinutes(5), null, summarize, progress, ct),
            ex => ex.Message, out _)!;
        await job.Completion;

        var snapshot = job.Snapshot();
        Assert.Equal(TranscriberJobState.Failed, snapshot.State);
        Assert.Equal(TranscriberChatTimeLimit.TimedOutMessage(Short), snapshot.Error);
        Assert.Equal(0, jobs.RunningCount);
        Assert.NotNull(jobs.TryStart("Summary", (_, _) => Task.FromResult("ok"), ex => ex.Message, out _));
    }

    [Fact]
    public async Task ALiveUpdateWhoseSummaryAtTheTopTimesOutKeepsTheNewSection()
    {
        var summarize = TranscriberChatTimeLimit.Apply(async (request, ct) =>
        {
            if (request.Kind == TranscriptSummaryKind.SectionNotes)
                return "- a new point";
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, Short);

        var result = await TranscriptNotesWriter.UpdateAsync(
            "SUMMARY SO FAR\nOld summary.\n\nNOTES BY TIME\n[00:00–05:00]\n- An old point.",
            "[05:10] Something new.", TimeSpan.FromMinutes(10), "Summary", summarize);

        Assert.True(result.SectionAdded);
        Assert.IsType<TimeoutException>(result.SummaryError);
        Assert.Contains("- a new point", result.Notes);
        Assert.Contains("Old summary.", result.Notes);
    }
}
