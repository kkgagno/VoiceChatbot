using VoiceChatbot;
using Xunit;

public class TranscriberJobsTests
{
    private static readonly Func<Exception, string> Describe = ex => "failed: " + ex.Message;

    [Fact]
    public async Task AJobRunsInTheBackgroundAndKeepsItsResult()
    {
        var jobs = new TranscriberJobs();
        var job = jobs.TryStart("Summary", (_, _) => Task.FromResult("the notes"), Describe, out var refusal);

        Assert.NotNull(job);
        Assert.Equal("", refusal);
        await job!.Completion;

        var snapshot = jobs.Get(job.Id)!.Snapshot();
        Assert.Equal(TranscriberJobState.Done, snapshot.State);
        Assert.Equal("the notes", snapshot.Result);
        Assert.Equal("Summary", snapshot.Label);
        Assert.Matches("^[0-9a-f]{32}$", snapshot.Id);
    }

    [Fact]
    public async Task ProgressIsVisibleWhileTheJobRuns()
    {
        var jobs = new TranscriberJobs();
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var job = jobs.TryStart("Summary", async (progress, _) =>
        {
            progress.Report("Summarizing part 2 of 5...");
            reported.SetResult();
            await release.Task;
            return "done";
        }, Describe, out _)!;

        await reported.Task;
        var running = job.Snapshot();
        Assert.Equal(TranscriberJobState.Running, running.State);
        Assert.Equal("Summarizing part 2 of 5...", running.Progress);

        release.SetResult();
        await job.Completion;
        Assert.Equal("", job.Snapshot().Progress);
    }

    [Fact]
    public async Task CancelStopsTheJob()
    {
        var jobs = new TranscriberJobs();
        var started = new TaskCompletionSource();
        var job = jobs.TryStart("Summary", async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        }, Describe, out _)!;

        await started.Task;
        Assert.True(jobs.Cancel(job.Id));
        await job.Completion;

        Assert.Equal(TranscriberJobState.Cancelled, job.State);
        Assert.False(jobs.Cancel(job.Id));
        Assert.False(jobs.Cancel("unknown"));
    }

    [Fact]
    public async Task AFailureIsDescribedForTheBrowser()
    {
        var jobs = new TranscriberJobs();
        var job = jobs.TryStart("Summary", (_, _) => throw new InvalidOperationException("Select a model first."), Describe, out _)!;
        await job.Completion;

        var snapshot = job.Snapshot();
        Assert.Equal(TranscriberJobState.Failed, snapshot.State);
        Assert.Equal("failed: Select a model first.", snapshot.Error);
    }

    [Fact]
    public async Task OnlyMaxRunningJobsRunAtOnce()
    {
        var jobs = new TranscriberJobs(maxRunning: 1);
        var release = new TaskCompletionSource<string>();
        var first = jobs.TryStart("Summary", (_, _) => release.Task, Describe, out _)!;

        Assert.Null(jobs.TryStart("Key points", (_, _) => Task.FromResult(""), Describe, out var refusal));
        Assert.Contains("already", refusal);

        release.SetResult("ok");
        await first.Completion;
        Assert.NotNull(jobs.TryStart("Key points", (_, _) => Task.FromResult(""), Describe, out _));
    }

    [Fact]
    public async Task FinishedJobsAreForgottenAfterAWhile()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var jobs = new TranscriberJobs(keepFinished: TimeSpan.FromMinutes(30), clock: () => now);
        var job = jobs.TryStart("Summary", (_, _) => Task.FromResult("x"), Describe, out _)!;
        await job.Completion;

        now = now.AddMinutes(29);
        Assert.NotNull(jobs.Get(job.Id));
        now = now.AddMinutes(2);
        Assert.Null(jobs.Get(job.Id));
        Assert.Null(jobs.Get(null));
    }

    [Fact]
    public async Task CancelAllStopsEveryRunningJob()
    {
        var jobs = new TranscriberJobs(maxRunning: 2);
        Task<string> Wait(IProgress<string> _, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => "", ct);
        var a = jobs.TryStart("Summary", Wait, Describe, out _)!;
        var b = jobs.TryStart("Summary", Wait, Describe, out _)!;

        jobs.CancelAll();
        await Task.WhenAll(a.Completion, b.Completion);

        Assert.Equal(TranscriberJobState.Cancelled, a.State);
        Assert.Equal(TranscriberJobState.Cancelled, b.State);
        Assert.Equal(0, jobs.RunningCount);
    }
}
