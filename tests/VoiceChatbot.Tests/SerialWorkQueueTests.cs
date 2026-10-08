using System.Collections.Concurrent;
using VoiceChatbot;
using Xunit;

public class SerialWorkQueueTests
{
    [Fact]
    public async Task RunsItemsOneAtATimeInOrder()
    {
        var queue = new SerialWorkQueue();
        var order = new ConcurrentQueue<int>();
        var running = 0;
        var overlapped = false;

        var tasks = Enumerable.Range(0, 20).Select(i => queue.Enqueue(() =>
        {
            if (Interlocked.Increment(ref running) > 1)
                overlapped = true;
            Thread.Sleep(i % 3);
            order.Enqueue(i);
            Interlocked.Decrement(ref running);
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.False(overlapped);
        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public async Task FailureOnlyFaultsThatItem()
    {
        var queue = new SerialWorkQueue();

        var failing = queue.Enqueue<int>(() => throw new IOException("disk full"));
        var next = queue.Enqueue(() => 42);

        await Assert.ThrowsAsync<IOException>(() => failing);
        Assert.Equal(42, await next);
    }

    [Fact]
    public async Task WhenIdleWaitsForQueuedWorkAndNeverFaults()
    {
        var queue = new SerialWorkQueue();
        var done = false;
        _ = queue.Enqueue(() => { Thread.Sleep(50); done = true; });
        _ = queue.Enqueue<int>(() => throw new InvalidOperationException());

        await queue.WhenIdleAsync();

        Assert.True(done);
    }
}
