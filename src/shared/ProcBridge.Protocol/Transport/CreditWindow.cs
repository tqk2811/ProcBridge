namespace ProcBridge.Protocol.Transport;

/// <summary>
/// Credit window for bulk transfers to one Agent (plan section 7): file data in flight in one direction, over every
/// connection of the pool together, may not exceed <see cref="Size"/> bytes. Without it each download kept 8 chunks of
/// 256 KB in flight, and several downloads together several megabytes, which on a slow link queue up in the socket
/// buffers and the network ahead of every metadata request (a listing waited seconds behind a download). The window
/// follows the link: <c>delivery rate × max(<see cref="QueueDelayMs"/>, 2 × round trip) + one transfer</c>, clamped to
/// [<see cref="MinSize"/>, <see cref="MaxSize"/>]. That is at least the bandwidth-delay product plus a chunk, so the
/// link stays full, and at most about a tenth of a second of data (or two round trips) queued in front of small requests.
/// The round trip is the least one seen while idle (<see cref="ObserveRoundTrip"/>, the Hello of each connection): the
/// latency of chunks themselves includes the queue the window creates and would make the window feed on itself.
/// A transfer larger than the window still goes when nothing else is in flight. Waiters are served in order.
/// </summary>
public sealed class CreditWindow
{
    /// <summary>Delivery rate is measured over intervals at least this long, while transfers ran the whole interval.</summary>
    private const long RateIntervalMs = 200;

    /// <summary>Data allowed to queue in front of small requests, in milliseconds of the measured rate.</summary>
    public const long QueueDelayMs = 100;

    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly Func<long> _clock;
    private long _size;
    private long _inFlight;
    private int _transfers;
    private long _intervalStart;
    private long _intervalBytes;
    private bool _idleInInterval = true;
    private double _rate;
    private long _roundTripMs;

    public CreditWindow(long initialSize, long minSize, long maxSize)
        : this(initialSize, minSize, maxSize, () => Environment.TickCount64)
    {
    }

    /// <param name="clock">Milliseconds, monotonic; tests drive it by hand.</param>
    internal CreditWindow(long initialSize, long minSize, long maxSize, Func<long> clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, minSize);
        MinSize = minSize;
        MaxSize = maxSize;
        _size = Math.Clamp(initialSize, minSize, maxSize);
        _clock = clock;
        _intervalStart = clock();
    }

    public long MinSize { get; }

    public long MaxSize { get; }

    /// <summary>Current window in bytes.</summary>
    public long Size
    {
        get
        {
            lock (_lock)
                return _size;
        }
    }

    /// <summary>Bytes acquired and not yet completed or released.</summary>
    public long InFlight
    {
        get
        {
            lock (_lock)
                return _inFlight;
        }
    }

    /// <summary>Delivery rate measured so far in bytes per second; 0 until a full interval was measured.</summary>
    public double Rate
    {
        get
        {
            lock (_lock)
                return _rate;
        }
    }

    /// <summary>Least round trip seen so far in milliseconds; 0 until one was observed.</summary>
    public long RoundTripMs
    {
        get
        {
            lock (_lock)
                return _roundTripMs;
        }
    }

    /// <summary>Feeds a round trip of a small exchange (keeps the least one).</summary>
    public void ObserveRoundTrip(TimeSpan roundTrip)
    {
        long ms = Math.Max(1, (long)roundTrip.TotalMilliseconds);
        lock (_lock)
        {
            if (_roundTripMs == 0 || ms < _roundTripMs)
                _roundTripMs = ms;
        }
    }

    /// <summary>
    /// Waits until <paramref name="bytes"/> fit in the window (or nothing else is in flight), then counts them in flight.
    /// Call <see cref="Lease.Complete"/> when the transfer arrived, or dispose the lease when it failed.
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<Waiter> node;
        lock (_lock)
        {
            if (_waiters.Count == 0 && Fits(bytes))
                return Grant(bytes);
            node = _waiters.AddLast(new Waiter(bytes));
        }
        using (cancellationToken.Register(() => Cancel(node, cancellationToken)))
            await node.Value.Completion.Task.ConfigureAwait(false);
        return new Lease(this, bytes);
    }

    private bool Fits(int bytes) => _inFlight == 0 || _inFlight + bytes <= _size;

    /// <summary>Counts <paramref name="bytes"/> in flight; under the lock.</summary>
    private Lease Grant(int bytes)
    {
        _inFlight += bytes;
        _transfers++;
        return new Lease(this, bytes);
    }

    private void Cancel(LinkedListNode<Waiter> node, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (node.List is null)
                return; // Granted already: the lease is released as usual.
            _waiters.Remove(node);
            // A large waiter at the head may have held back smaller ones that fit.
            WakeWaiters();
        }
        node.Value.Completion.TrySetCanceled(cancellationToken);
    }

    private void Finish(int bytes, bool delivered)
    {
        lock (_lock)
        {
            long now = _clock();
            _inFlight -= bytes;
            _transfers--;
            if (delivered)
                Measure(bytes, now);
            if (_transfers == 0)
                _idleInInterval = true;
            WakeWaiters();
        }
    }

    /// <summary>Updates the rate and the window from one delivered transfer; under the lock.</summary>
    private void Measure(int bytes, long now)
    {
        _intervalBytes += bytes;
        long elapsed = now - _intervalStart;
        if (elapsed >= RateIntervalMs)
        {
            // An interval with an idle moment says nothing about the link, only about the demand.
            if (!_idleInInterval)
            {
                double sample = _intervalBytes * 1000.0 / elapsed;
                _rate = _rate == 0 ? sample : 0.75 * _rate + 0.25 * sample;
            }
            _intervalStart = now;
            _intervalBytes = 0;
            _idleInInterval = _transfers == 0;
        }

        if (_rate > 0)
        {
            double delay = Math.Max(QueueDelayMs, 2 * _roundTripMs) / 1000.0;
            _size = (long)Math.Clamp(_rate * delay + bytes, MinSize, MaxSize);
        }
    }

    /// <summary>Grants waiters in order while they fit; under the lock.</summary>
    private void WakeWaiters()
    {
        while (_waiters.First is { } first && Fits(first.Value.Bytes))
        {
            _waiters.RemoveFirst();
            _inFlight += first.Value.Bytes;
            _transfers++;
            first.Value.Completion.TrySetResult();
        }
    }

    /// <summary>Bytes of one transfer counted in flight; <see cref="Complete"/> or dispose exactly once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly CreditWindow _window;
        private readonly int _bytes;
        private int _done;

        internal Lease(CreditWindow window, int bytes)
        {
            _window = window;
            _bytes = bytes;
        }

        /// <summary>The transfer arrived: frees the bytes and feeds the delivery rate.</summary>
        public void Complete()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _window.Finish(_bytes, delivered: true);
        }

        /// <summary>Frees the bytes without a measurement (failed or cancelled transfer); no-op after <see cref="Complete"/>.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                _window.Finish(_bytes, delivered: false);
        }
    }

    private sealed class Waiter(int bytes)
    {
        public int Bytes { get; } = bytes;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
