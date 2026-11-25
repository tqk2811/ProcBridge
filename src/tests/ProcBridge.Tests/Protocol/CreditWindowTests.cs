using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

/// <summary>
/// Credit window of bulk data (plan section 7): file data in flight is bounded so metadata does not queue behind it,
/// and the bound follows the link (twice rate × least latency) between its limits.
/// </summary>
[TestClass]
public sealed class CreditWindowTests
{
    private const int Chunk = 256 * 1024;

    [TestMethod]
    public async Task Full_WaitsUntilBytesComplete_InOrder()
    {
        var window = new CreditWindow(2 * Chunk, Chunk, 16 * Chunk);
        CreditWindow.Lease a = await window.AcquireAsync(Chunk, CancellationToken.None);
        CreditWindow.Lease b = await window.AcquireAsync(Chunk, CancellationToken.None);
        Task<CreditWindow.Lease> c = window.AcquireAsync(Chunk, CancellationToken.None).AsTask();
        Task<CreditWindow.Lease> d = window.AcquireAsync(1, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.IsFalse(c.IsCompleted);
        // A small waiter does not overtake a waiting larger one.
        Assert.IsFalse(d.IsCompleted);
        Assert.AreEqual(2L * Chunk, window.InFlight);

        a.Complete();
        (await c.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        b.Dispose();
        (await d.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.AreEqual(0, window.InFlight);
    }

    [TestMethod]
    public async Task TransferLargerThanTheWindow_GoesAlone()
    {
        var window = new CreditWindow(Chunk, Chunk, Chunk);
        using CreditWindow.Lease big = await window.AcquireAsync(4 * Chunk, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(4L * Chunk, window.InFlight);
    }

    [TestMethod]
    public async Task CancelledWaiter_LetsTheNextOneIn()
    {
        var window = new CreditWindow(2 * Chunk, Chunk, 2 * Chunk);
        CreditWindow.Lease held = await window.AcquireAsync(Chunk, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        Task<CreditWindow.Lease> big = window.AcquireAsync(2 * Chunk, cancel.Token).AsTask();
        Task<CreditWindow.Lease> small = window.AcquireAsync(Chunk, CancellationToken.None).AsTask();
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => big);
        (await small.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        held.Dispose();
        Assert.AreEqual(0, window.InFlight);
    }

    /// <summary>
    /// A slow link (1.25 MB/s, 100 ms): two megabytes in flight would be 1.6 s of queue in front of every metadata
    /// request; the window shrinks to its floor. A fast far link (125 MB/s, 100 ms) grows it to its ceiling. A medium
    /// one (13 MB/s, 100 ms) lands in between: rate × two round trips plus one chunk.
    /// </summary>
    [TestMethod]
    [DataRow(1_250_000.0, 1L * 1024 * 1024)]
    [DataRow(125_000_000.0, 16L * 1024 * 1024)]
    [DataRow(13_107_200.0, 13_107_200L / 5 + Chunk)]
    public async Task Window_FollowsTheLink(double bytesPerSecond, long expected)
    {
        long now = 0;
        var window = new CreditWindow(8 * Chunk, 4 * Chunk, 64 * Chunk, () => now);
        const long RttMs = 100;
        window.ObserveRoundTrip(TimeSpan.FromMilliseconds(RttMs));
        // Keep the window full for ten simulated seconds: each chunk arrives after the round trip plus its transfer
        // time at the link rate, and the next chunk goes as soon as one completes.
        var inFlight = new Queue<(CreditWindow.Lease Lease, long DoneAt)>();
        long linkFreeAt = 0;
        while (now < 10_000)
        {
            while (inFlight.Count == 0 || window.InFlight + Chunk <= window.Size)
            {
                CreditWindow.Lease lease = await window.AcquireAsync(Chunk, CancellationToken.None);
                linkFreeAt = Math.Max(linkFreeAt, now + RttMs / 2) + (long)(Chunk * 1000.0 / bytesPerSecond);
                inFlight.Enqueue((lease, linkFreeAt + RttMs / 2));
            }
            (CreditWindow.Lease next, long doneAt) = inFlight.Dequeue();
            now = Math.Max(now, doneAt);
            next.Complete();
        }
        while (inFlight.Count > 0)
            inFlight.Dequeue().Lease.Complete();

        // The rate is an average of measured intervals: allow 3 %.
        Assert.AreEqual(expected, window.Size, (long)(expected * 0.03), $"rate {window.Rate:0} B/s");
    }
}
