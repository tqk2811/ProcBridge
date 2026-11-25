using System.Diagnostics;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

/// <summary>
/// Flow control on one connection (plan section 7): small frames (metadata, requests, pings) go before file data that
/// waits for the same connection, without starving the data.
/// </summary>
[TestClass]
public sealed class FrameSendGateTests
{
    private const int Big = 256 * 1024;

    /// <summary>Records what is written; the first write blocks until <see cref="Open"/>.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _written = new();
        private int _writes;

        public void Open() => _open.TrySetResult();

        public byte[] Written
        {
            get
            {
                lock (_written)
                    return _written.ToArray();
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == 1)
                await _open.Task.WaitAsync(cancellationToken);
            lock (_written)
                _written.Write(buffer.Span);
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Frame MakeFrame(uint id, int payloadSize)
        => new() { Kind = FrameKind.Response, MessageType = MessageType.FsRead, RequestId = id, Header = new byte[8], Payload = new byte[payloadSize] };

    private static async Task<List<uint>> ReadOrderAsync(byte[] wire)
    {
        var order = new List<uint>();
        await using var reader = new StreamFrameChannel(new MemoryStream(wire), ownsStream: true, new TransportOptions().MaxFrameSize);
        while (await reader.ReceiveAsync(CancellationToken.None) is { } frame)
            order.Add(frame.RequestId);
        return order;
    }

    private static async Task WaitForWaitersAsync(StreamFrameChannel channel, int count)
    {
        var watch = Stopwatch.StartNew();
        while (channel.WaitingSendCount < count)
        {
            Assert.IsLessThan(10_000, watch.ElapsedMilliseconds, "frames did not queue");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task SmallFrame_GoesBeforeFileDataWaitingOnTheSameConnection()
    {
        var stream = new GatedStream();
        await using var channel = new StreamFrameChannel(stream, ownsStream: false, new TransportOptions().MaxFrameSize);

        // Frame 1 (data) is on the wire and blocked; frame 2 (more data) waits; then frame 3, a metadata answer, arrives.
        ValueTask first = channel.SendAsync(MakeFrame(1, Big), CancellationToken.None);
        await WaitForWaitersAsync(channel, 0);
        Task second = channel.SendAsync(MakeFrame(2, Big), CancellationToken.None).AsTask();
        await WaitForWaitersAsync(channel, 1);
        Task third = channel.SendAsync(MakeFrame(3, 100), CancellationToken.None).AsTask();
        await WaitForWaitersAsync(channel, 2);

        stream.Open();
        await first;
        await Task.WhenAll(second, third);

        CollectionAssert.AreEqual(new uint[] { 1, 3, 2 }, await ReadOrderAsync(stream.Written));
    }

    [TestMethod]
    public async Task FileData_IsNotStarvedBySmallFrames()
    {
        var stream = new GatedStream();
        await using var channel = new StreamFrameChannel(stream, ownsStream: false, new TransportOptions().MaxFrameSize);

        ValueTask first = channel.SendAsync(MakeFrame(1, 100), CancellationToken.None);
        var waiting = new List<Task> { channel.SendAsync(MakeFrame(2, Big), CancellationToken.None).AsTask() };
        await WaitForWaitersAsync(channel, 1);
        int smallCount = FrameSendGate.MaxUrgentInARow + 4;
        for (int i = 0; i < smallCount; i++)
            waiting.Add(channel.SendAsync(MakeFrame((uint)(10 + i), 100), CancellationToken.None).AsTask());
        await WaitForWaitersAsync(channel, 1 + smallCount);

        stream.Open();
        await first;
        await Task.WhenAll(waiting);

        List<uint> order = await ReadOrderAsync(stream.Written);
        // The data frame goes right after MaxUrgentInARow small frames, not after all of them.
        Assert.AreEqual(1 + FrameSendGate.MaxUrgentInARow, order.IndexOf(2));
    }

    [TestMethod]
    public async Task CancelledWaiter_LeavesTheQueue()
    {
        var gate = new FrameSendGate();
        await gate.WaitAsync(urgent: false, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        Task cancelled = gate.WaitAsync(urgent: true, cancel.Token);
        Task next = gate.WaitAsync(urgent: false, CancellationToken.None);
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
        Assert.AreEqual(1, gate.WaitingCount);

        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        Assert.AreEqual(0, gate.WaitingCount);
        await gate.WaitAsync(urgent: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
