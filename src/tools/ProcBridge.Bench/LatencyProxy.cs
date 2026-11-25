using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ProcBridge.Bench;

/// <summary>
/// TCP proxy on loopback that imitates a network link: every byte reaches the other side half the round trip time
/// after it was sent, and each direction carries at most <c>bytesPerSecond</c> shared by every connection (bytes queue
/// behind each other like on a real link). Sits between the Broker and the Agent so the whole stack (pool, framing, crypto off) sees the latency.
/// </summary>
internal sealed class LatencyProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly TimeSpan _oneWay;
    private readonly double _bytesPerSecond;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _links = [];
    private readonly Task _accepting;
    private readonly object _linkLock = new();
    private long _upFreeTicks;
    private long _downFreeTicks;
    private long _bytesUp;
    private long _bytesDown;

    /// <param name="bytesPerSecond">Bandwidth of each direction; 0 means unlimited.</param>
    public LatencyProxy(IPEndPoint target, TimeSpan roundTrip, double bytesPerSecond)
    {
        _target = target;
        _oneWay = roundTrip / 2;
        _bytesPerSecond = bytesPerSecond;
        _listener.Start();
        _accepting = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Bytes carried from the Broker to the Agent so far.</summary>
    public long BytesUp => Interlocked.Read(ref _bytesUp);

    /// <summary>Bytes carried from the Agent to the Broker so far.</summary>
    public long BytesDown => Interlocked.Read(ref _bytesDown);

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                lock (_links)
                    _links.Add(Task.Run(() => LinkAsync(client)));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task LinkAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        {
            client.NoDelay = true;
            server.NoDelay = true;
            try
            {
                await server.ConnectAsync(_target, _stopping.Token).ConfigureAwait(false);
                using var link = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                Task up = PumpAsync(client.GetStream(), server.GetStream(), isUp: true, link.Token);
                Task down = PumpAsync(server.GetStream(), client.GetStream(), isUp: false, link.Token);
                await Task.WhenAny(up, down).ConfigureAwait(false);
                link.Cancel();
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// One direction: the reader stamps each chunk with the time it would arrive (after the bytes ahead of it went out
    /// at the link rate, plus the one-way delay); the writer sends it at that time.
    /// </summary>
    private async Task PumpAsync(NetworkStream source, NetworkStream destination, bool isUp, CancellationToken cancellationToken)
    {
        var queue = Channel.CreateUnbounded<(byte[] Data, int Length, long DueTicks)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        Task writer = WriteAsync(queue.Reader, destination, cancellationToken);
        try
        {
            while (true)
            {
                byte[] buffer = new byte[64 * 1024];
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (isUp)
                    Interlocked.Add(ref _bytesUp, read);
                else
                    Interlocked.Add(ref _bytesDown, read);
                long due = Reserve(isUp, read) + (long)(_oneWay.TotalSeconds * Stopwatch.Frequency);
                queue.Writer.TryWrite((buffer, read, due));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
            // Ends the other side too: like a TCP close travelling through the link.
            destination.Socket.Shutdown(SocketShutdown.Send);
        }
    }

    /// <summary>Books the link of one direction for <paramref name="bytes"/>; returns when their last bit has left.</summary>
    private long Reserve(bool isUp, int bytes)
    {
        long sendTicks = _bytesPerSecond > 0 ? (long)(bytes / _bytesPerSecond * Stopwatch.Frequency) : 0;
        long now = Stopwatch.GetTimestamp();
        lock (_linkLock)
        {
            ref long free = ref isUp ? ref _upFreeTicks : ref _downFreeTicks;
            free = Math.Max(free, now) + sendTicks;
            return free;
        }
    }

    private static async Task WriteAsync(ChannelReader<(byte[] Data, int Length, long DueTicks)> queue, NetworkStream destination, CancellationToken cancellationToken)
    {
        await foreach ((byte[] data, int length, long due) in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
            if (wait > TimeSpan.Zero)
                await PreciseDelayAsync(wait, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(data.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Task.Delay rounds to the timer resolution (~15 ms); spin the last stretch for sub-millisecond delays.</summary>
    private static async Task PreciseDelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        long due = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        if (wait > TimeSpan.FromMilliseconds(20))
            await Task.Delay(wait - TimeSpan.FromMilliseconds(16), cancellationToken).ConfigureAwait(false);
        while (Stopwatch.GetTimestamp() < due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        Task[] links;
        lock (_links)
            links = _links.ToArray();
        await Task.WhenAll(links).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
