using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// The time limit of each chat-model request a web transcriber job makes (Summarize / Re-summarize all, a
/// live-notes update, the notes on Stop). Without it a chat server that stalls kept a job "running" for as long as
/// the HTTP client's own timeout (20 minutes) per request, with nothing to tell the phone why. The limit is linked
/// to the job's cancellation token, so the HTTP request itself is cancelled. The desktop chat and the desktop
/// Live Transcriber do not use it.
/// </summary>
public static class TranscriberChatTimeLimit
{
    /// <summary>How long one chat-model request of a web transcriber job may take.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>What a request that ran into <paramref name="limit"/> fails with (and so the job, shown on the page).</summary>
    public static string TimedOutMessage(TimeSpan limit) =>
        $"The chat model on the PC did not answer within {Describe(limit)}.";

    /// <summary>
    /// <paramref name="summarize"/> with a time limit per request (<see cref="Limit"/> unless given). A request that
    /// takes longer is cancelled and throws <see cref="TimeoutException"/> with <see cref="TimedOutMessage"/>; it
    /// ends on time even when the request does not stop at once (still queued on the UI thread, say). Cancelling
    /// the caller's token still throws <see cref="OperationCanceledException"/>, and other errors are passed on.
    /// </summary>
    public static Func<TranscriptSummaryRequest, CancellationToken, Task<string>> Apply(
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        TimeSpan? limit = null)
    {
        ArgumentNullException.ThrowIfNull(summarize);
        var timeLimit = limit ?? Limit;
        if (timeLimit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return async (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeLimit);
            Task<string>? pending = null;
            try
            {
                pending = summarize(request, timeout.Token);
                return await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(TimedOutMessage(timeLimit), ex);
            }
            finally
            {
                // A request left behind by WaitAsync may still fail later; nobody waits for it any more.
                if (pending is { IsCompleted: false })
                    _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        };
    }

    private static string Describe(TimeSpan limit)
    {
        if (limit.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            var minutes = limit.Ticks / TimeSpan.TicksPerMinute;
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        var seconds = Math.Max(1, (long)Math.Ceiling(limit.TotalSeconds));
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }
}
