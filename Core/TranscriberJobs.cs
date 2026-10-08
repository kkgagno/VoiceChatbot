using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public enum TranscriberJobState
{
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>What a browser polling a job is told about it.</summary>
public sealed record TranscriberJobSnapshot(
    string Id,
    string Label,
    TranscriberJobState State,
    string Progress,
    string Result,
    string Error);

/// <summary>
/// One background request of the web transcriber (a full Summarize, which can take many minutes for a long
/// transcript). The browser starts it, then polls for progress and the result instead of holding one HTTP
/// request open, so a phone that sleeps for a moment does not lose the summary. Thread-safe.
/// </summary>
public sealed class TranscriberJob
{
    private readonly object _gate = new();
    // Not disposed: it has no timer, and disposing it could race with Cancel on another thread.
    private readonly CancellationTokenSource _cts = new();
    private TranscriberJobState _state = TranscriberJobState.Running;
    private string _progress = "";
    private string _result = "";
    private string _error = "";
    private DateTimeOffset? _finished;

    internal TranscriberJob(string id, string label, DateTimeOffset started)
    {
        Id = id;
        Label = label;
        Started = started;
    }

    public string Id { get; }

    /// <summary>What the job is for (the summary style).</summary>
    public string Label { get; }

    public DateTimeOffset Started { get; }

    /// <summary>Completes when the job has finished, whatever the outcome. Never faults.</summary>
    public Task Completion { get; internal set; } = Task.CompletedTask;

    public TranscriberJobState State
    {
        get { lock (_gate) return _state; }
    }

    public DateTimeOffset? Finished
    {
        get { lock (_gate) return _finished; }
    }

    internal CancellationToken Token => _cts.Token;

    public TranscriberJobSnapshot Snapshot()
    {
        lock (_gate)
            return new TranscriberJobSnapshot(Id, Label, _state, _progress, _result, _error);
    }

    /// <summary>Asks the job to stop. False when it has already finished.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (_state != TranscriberJobState.Running)
                return false;
        }

        // Outside the lock: cancellation callbacks run synchronously and may finish the job.
        _cts.Cancel();
        return true;
    }

    internal void ReportProgress(string? message)
    {
        lock (_gate)
        {
            if (_state == TranscriberJobState.Running)
                _progress = message ?? "";
        }
    }

    internal void Finish(TranscriberJobState state, string result, string error, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_state != TranscriberJobState.Running)
                return;

            _state = state;
            _result = result;
            _error = error;
            _progress = "";
            _finished = at;
        }
    }

    internal sealed class ProgressSink : IProgress<string>
    {
        private readonly TranscriberJob _job;

        public ProgressSink(TranscriberJob job) => _job = job;

        public void Report(string value) => _job.ReportProgress(value);
    }
}

/// <summary>
/// The web transcriber's background jobs: at most <see cref="MaxRunning"/> run at once (each one keeps the chat
/// model busy), and finished ones are forgotten after <see cref="KeepFinished"/>. Thread-safe.
/// </summary>
public sealed class TranscriberJobs
{
    private readonly ConcurrentDictionary<string, TranscriberJob> _jobs = new(StringComparer.Ordinal);
    private readonly object _startGate = new();
    private readonly Func<DateTimeOffset> _clock;

    public TranscriberJobs(int maxRunning = 2, TimeSpan? keepFinished = null, Func<DateTimeOffset>? clock = null)
    {
        if (maxRunning < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRunning));

        MaxRunning = maxRunning;
        KeepFinished = keepFinished ?? TimeSpan.FromMinutes(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxRunning { get; }

    public TimeSpan KeepFinished { get; }

    public int RunningCount => _jobs.Values.Count(job => job.State == TranscriberJobState.Running);

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool, or returns null with <paramref name="refusal"/> set when
    /// <see cref="MaxRunning"/> jobs are already running. <paramref name="describeError"/> turns a failure into the
    /// message the browser shows.
    /// </summary>
    public TranscriberJob? TryStart(
        string label,
        Func<IProgress<string>, CancellationToken, Task<string>> work,
        Func<Exception, string> describeError,
        out string refusal)
    {
        Prune();
        TranscriberJob job;
        lock (_startGate)
        {
            if (RunningCount >= MaxRunning)
            {
                refusal = MaxRunning == 1
                    ? "A summary is already being written. Wait for it or cancel it first."
                    : $"{MaxRunning} summaries are already being written. Wait for one to finish or cancel it first.";
                return null;
            }

            job = new TranscriberJob(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), label, _clock());
            _jobs[job.Id] = job;
            job.Completion = Task.Run(() => RunAsync(job, work, describeError));
        }

        refusal = "";
        return job;
    }

    /// <summary>The job with this id, or null when it is unknown or was forgotten.</summary>
    public TranscriberJob? Get(string? id)
    {
        Prune();
        return id != null && _jobs.TryGetValue(id, out var job) ? job : null;
    }

    /// <summary>Cancels the job with this id. False when it is unknown or already finished.</summary>
    public bool Cancel(string? id) => Get(id)?.Cancel() ?? false;

    /// <summary>Cancels every running job (the remote is stopping).</summary>
    public void CancelAll()
    {
        foreach (var job in _jobs.Values)
            job.Cancel();
    }

    /// <summary>Forgets jobs that finished more than <see cref="KeepFinished"/> ago.</summary>
    public void Prune()
    {
        var cutoff = _clock() - KeepFinished;
        foreach (var pair in _jobs)
        {
            if (pair.Value.Finished is { } finished && finished < cutoff)
                _jobs.TryRemove(pair.Key, out _);
        }
    }

    private async Task RunAsync(
        TranscriberJob job,
        Func<IProgress<string>, CancellationToken, Task<string>> work,
        Func<Exception, string> describeError)
    {
        try
        {
            var result = await work(new TranscriberJob.ProgressSink(job), job.Token).ConfigureAwait(false);
            if (job.Token.IsCancellationRequested)
                job.Finish(TranscriberJobState.Cancelled, "", "", _clock());
            else
                job.Finish(TranscriberJobState.Done, result ?? "", "", _clock());
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Finish(TranscriberJobState.Cancelled, "", "", _clock());
        }
        catch (Exception ex)
        {
            string message;
            try
            {
                message = describeError(ex);
            }
            catch
            {
                message = ex.Message;
            }

            job.Finish(TranscriberJobState.Failed, "", string.IsNullOrWhiteSpace(message) ? "The request failed." : message, _clock());
        }
    }
}
