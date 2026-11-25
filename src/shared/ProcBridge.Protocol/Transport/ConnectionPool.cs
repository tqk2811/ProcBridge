using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Protocol.Transport;

/// <summary>
/// Keeps up to <see cref="TransportOptions.ConnectionCount"/> connections to one Agent and spreads requests over
/// them: an idle connection is used first, otherwise a new one is opened (lazy growth, and reconnect of slots whose
/// connection died), otherwise the least busy one. Idempotent requests whose connection broke are resent.
/// Owns the connections it opens.
/// </summary>
public sealed class ConnectionPool : IAsyncDisposable
{
    private readonly IConnectionFactory _factory;
    private readonly HelloRequest _hello;
    private readonly TransportOptions _options;
    private readonly Slot[] _slots;
    private long _connectCount;
    private long _leastRoundTripTicks;
    private int _disposed;

    public ConnectionPool(IConnectionFactory factory, HelloRequest hello, TransportOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ConnectionCount, 1);
        _factory = factory;
        _hello = hello;
        _options = options;
        _slots = new Slot[options.ConnectionCount];
        for (int i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
    }

    public TransportOptions Options => _options;

    /// <summary>Total number of successful connects (first connects and reconnects).</summary>
    public long ConnectCount => Interlocked.Read(ref _connectCount);

    /// <summary>Least Hello round trip of the connections opened so far (base latency of the link); null before the first.</summary>
    public TimeSpan? LeastRoundTrip
    {
        get
        {
            long ticks = Interlocked.Read(ref _leastRoundTripTicks);
            return ticks == 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>Completed requests per slot, for diagnostics and tests.</summary>
    public long[] GetRequestsPerSlot() => _slots.Select(x => Interlocked.Read(ref x.Requests)).ToArray();

    /// <summary>Largest read the server serves in one response, from the Hello exchange.</summary>
    public async Task<int> GetMaxReadSizeAsync(CancellationToken cancellationToken)
    {
        (_, ProtocolConnection connection) = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return connection.Server.MaxReadSize;
    }

    /// <summary>The Hello answer of a pooled connection (opening one when none is alive).</summary>
    public async Task<HelloResponse> GetServerHelloAsync(CancellationToken cancellationToken)
    {
        (_, ProtocolConnection connection) = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return connection.Server;
    }

    public async Task<Frame> SendAsync(MessageType type, ReadOnlyMemory<byte> header, ReadOnlyMemory<byte> payload, bool idempotent, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        for (int attempt = 0; ; attempt++)
        {
            // A dispose that kills the connection under a request must not make the retry reconnect.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            (Slot slot, ProtocolConnection connection) = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Frame frame = await connection.SendAsync(type, header, payload, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref slot.Requests);
                return frame;
            }
            catch (ProtocolStatusException e) when (e.IsConnectionFailure && idempotent && attempt < _options.MaxRetries)
            {
                // Retry: AcquireAsync skips the dead connection and reopens its slot.
            }
        }
    }

    private async Task<(Slot, ProtocolConnection)> AcquireAsync(CancellationToken cancellationToken)
    {
        Slot? best = null;
        ProtocolConnection? bestConnection = null;
        Slot? empty = null;
        foreach (Slot slot in _slots)
        {
            ProtocolConnection? connection = slot.Connection;
            if (connection is { IsAlive: true })
            {
                if (bestConnection is null || connection.PendingCount < bestConnection.PendingCount)
                {
                    best = slot;
                    bestConnection = connection;
                }
            }
            else
            {
                empty ??= slot;
            }
        }

        if (bestConnection is not null && (bestConnection.PendingCount == 0 || empty is null))
            return (best!, bestConnection);

        // From here a slot is free. With a live connection to fall back on, growing the pool is optional: do not
        // queue behind another request's connect and do not hammer a slot that just failed.
        bool optional = bestConnection is not null;
        if (optional && empty!.IsResting(_options.ConnectBackoff))
            return (best!, bestConnection!);

        try
        {
            ProtocolConnection? opened = await ConnectSlotAsync(empty!, optional, cancellationToken).ConfigureAwait(false);
            return opened is null ? (best!, bestConnection!) : (empty!, opened);
        }
        catch (ProtocolStatusException) when (bestConnection is { IsAlive: true })
        {
            return (best!, bestConnection);
        }
    }

    /// <summary>Opens the connection of <paramref name="slot"/>; null when <paramref name="optional"/> and another request holds the slot.</summary>
    private async Task<ProtocolConnection?> ConnectSlotAsync(Slot slot, bool optional, CancellationToken cancellationToken)
    {
        if (optional)
        {
            if (!slot.Lock.Wait(0, CancellationToken.None))
                return null;
        }
        else
        {
            await slot.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ProtocolConnection? current = slot.Connection;
            if (current is { IsAlive: true })
                return current;
            slot.Connection = null;
            if (current is not null)
                await current.DisposeAsync().ConfigureAwait(false);

            ProtocolConnection connection;
            try
            {
                connection = await ProtocolConnection.OpenAsync(_factory, _hello, _options, cancellationToken).ConfigureAwait(false);
            }
            catch (ProtocolStatusException)
            {
                slot.MarkFailed();
                throw;
            }
            Interlocked.Increment(ref _connectCount);
            long ticks = Math.Max(1, connection.HelloRoundTrip.Ticks);
            for (long least = Interlocked.Read(ref _leastRoundTripTicks); least == 0 || ticks < least; least = Interlocked.Read(ref _leastRoundTripTicks))
            {
                if (Interlocked.CompareExchange(ref _leastRoundTripTicks, ticks, least) == least)
                    break;
            }
            slot.Connection = connection;
            slot.ClearFailure();
            // The store above must not be reordered after the load below, or this side and Dispose could miss each other.
            Interlocked.MemoryBarrier();
            // Dispose may have walked the slots before the line above; then nobody else would close this one.
            if (Volatile.Read(ref _disposed) != 0)
            {
                slot.Connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ConnectionPool));
            }
            return connection;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (Slot slot in _slots)
        {
            if (slot.Connection is { } connection)
                await connection.DisposeAsync().ConfigureAwait(false);
            slot.Connection = null;
        }
    }

    private sealed class Slot
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public volatile ProtocolConnection? Connection;
        public long Requests;
        private long _failedAt;

        public void MarkFailed() => Volatile.Write(ref _failedAt, Environment.TickCount64 | 1);

        public void ClearFailure() => Volatile.Write(ref _failedAt, 0);

        /// <summary>True when the last connect failed less than <paramref name="backoff"/> ago.</summary>
        public bool IsResting(TimeSpan backoff)
        {
            long failedAt = Volatile.Read(ref _failedAt);
            return failedAt != 0 && Environment.TickCount64 - failedAt < backoff.TotalMilliseconds;
        }
    }
}
