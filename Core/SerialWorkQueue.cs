using System;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// Runs work items one at a time, in the order they were queued, on the thread pool. A failed
/// item does not stop the ones after it; its exception goes to whoever awaits that item.
/// </summary>
public sealed class SerialWorkQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var task = _tail.ContinueWith(
                _ => work(),
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
            _tail = task;
            return task;
        }
    }

    public Task Enqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Enqueue(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>Completes (never faults) once everything queued so far has run.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return _tail.ContinueWith(
                _ => { },
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }
    }
}
