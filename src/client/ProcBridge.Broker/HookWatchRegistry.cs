using System.Collections.Concurrent;
using System.Threading.Channels;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Directory watches the hook registered for NtNotifyChangeDirectoryFile[Ex] (plan section 10, M7). Each watch
/// queues the changes of <see cref="FileCache.RemoteChanged"/> that fall inside its directory until the hook collects
/// them with a long poll: WaitAnyChanges collects every watch of a process at once (the hook keeps one pipe instance for
/// it), WaitChanges a single watch. A rescan, a reconnect of the change watcher or a full queue marks the watch as
/// overflowed, which the hook turns into <c>STATUS_NOTIFY_ENUM_DIR</c>.
/// Every watch belongs to the client process that registered it (the owner, see <see cref="Attach"/>): only that
/// process can wait on or remove it, and the watches of a process go away when its last pipe connection closes.
/// Notifications are handed to a task of their own, so the thread that raises <see cref="FileCache.RemoteChanged"/>
/// (the reader of the change connection) never waits for the watches.
/// Does not own the cache; subscribes to it until disposed.
/// </summary>
public sealed class HookWatchRegistry : IDisposable
{
    /// <summary>Changes kept per watch; beyond that the watch overflows (like a full notify buffer).</summary>
    public const int MaxQueuedChanges = 1024;

    /// <summary>Watches one session may hold; protects the Broker from a process that never closes handles.</summary>
    public const int MaxWatches = 4096;

    private readonly ConcurrentDictionary<uint, Watch> _watches = new();
    private readonly ConcurrentDictionary<long, OwnerSignal> _signals = new();
    private readonly Dictionary<long, int> _connections = [];
    private readonly object _connectionsLock = new();
    private int _nextId;

    private readonly int _replyBudget;
    private readonly int _anyReplyBudget;
    private readonly FileCache _cache;
    private readonly Channel<FsChangeNotification> _notifications = Channel.CreateUnbounded<FsChangeNotification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _dispatcher;

    /// <param name="replyBudget">Bytes of changes one WaitChanges reply may carry; tests shrink it.</param>
    public HookWatchRegistry(FileCache cache, int replyBudget = DefaultReplyBudget)
    {
        _replyBudget = replyBudget;
        _anyReplyBudget = replyBudget + HookWaitChangesReply.FixedSize - HookWaitAnyChangesReply.FixedSize;
        _cache = cache;
        _dispatcher = Task.Run(DispatchAsync);
        cache.RemoteChanged += OnRemoteChanged;
    }

    /// <summary>Test hook, called by the dispatching task before it applies a notification.</summary>
    internal Action? BeforeDispatch { get; set; }

    private int _dispatched;

    /// <summary>Notifications the dispatching task has applied so far; tests wait on it for the connect rescan.</summary>
    internal int DispatchedCount => Volatile.Read(ref _dispatched);

    /// <summary>Stops listening to the cache; the watches stay but receive nothing more.</summary>
    public void Dispose()
    {
        _cache.RemoteChanged -= OnRemoteChanged;
        _notifications.Writer.TryComplete();
    }

    /// <summary>What fits a reply message after its header and fixed fields.</summary>
    public const int DefaultReplyBudget = HookIpcProtocol.MaxMessageSize - HookIpcProtocol.HeaderSize - HookWaitChangesReply.FixedSize;

    public int Count => _watches.Count;

    /// <summary>A pipe connection of client <paramref name="owner"/> opened (one process may hold several).</summary>
    public void Attach(long owner)
    {
        lock (_connectionsLock)
            _connections[owner] = _connections.GetValueOrDefault(owner) + 1;
    }

    /// <summary>
    /// A pipe connection of <paramref name="owner"/> closed. When it was the owner's last one, every watch of the
    /// owner is removed (a blocked WaitChanges on it returns), since nothing can collect or remove them any more.
    /// </summary>
    public void Detach(long owner)
    {
        lock (_connectionsLock)
        {
            int left = _connections.GetValueOrDefault(owner) - 1;
            if (left > 0)
            {
                _connections[owner] = left;
                return;
            }
            _connections.Remove(owner);
            if (_signals.TryRemove(owner, out OwnerSignal? signal))
                signal.Set(); // A WaitAnyChanges still blocked returns empty.
            // Under the lock so an Attach that follows cannot see this owner's old watches.
            foreach (KeyValuePair<uint, Watch> pair in _watches)
            {
                if (pair.Value.Owner == owner && _watches.TryRemove(pair.Key, out Watch? watch))
                    watch.Close();
            }
        }
    }

    /// <summary>Starts watching the share directory <paramref name="remoteDirectory"/> for <paramref name="owner"/>; returns the watch id (never 0).</summary>
    public uint Add(long owner, string remoteDirectory, bool subtree)
    {
        if (_watches.Count >= MaxWatches)
            throw new ProtocolStatusException(NtStatus.InsufficientResources, "Too many directory watches.");
        uint id;
        do
            id = (uint)Interlocked.Increment(ref _nextId);
        while (id == 0);
        OwnerSignal signal = GetSignal(owner);
        _watches[id] = new Watch(owner, remoteDirectory, subtree, signal);
        // A WaitAnyChanges of the owner in progress returns empty, so the next poll covers the new watch too.
        signal.Set();
        return id;
    }

    /// <summary>Removes the watch of <paramref name="owner"/>; a WaitChanges blocked on it returns empty.</summary>
    /// <remarks>An id that belongs to another owner is reported as unknown and left alone.</remarks>
    public void Remove(long owner, uint id)
    {
        if (!_watches.TryGetValue(id, out Watch? watch) || watch.Owner != owner
            || !_watches.TryRemove(new KeyValuePair<uint, Watch>(id, watch)))
            throw UnknownWatch(id);
        watch.Close();
    }

    /// <summary>
    /// Waits until watch <paramref name="id"/> has changes or <paramref name="timeout"/> passes, then returns as many
    /// queued changes as fit a reply. <see cref="Timeout.InfiniteTimeSpan"/> waits until a change or removal.
    /// </summary>
    public async Task<HookWaitChangesReply> WaitAsync(long owner, uint id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_watches.TryGetValue(id, out Watch? watch) || watch.Owner != owner)
            throw UnknownWatch(id);
        Task ready = watch.WhenReady();
        if (!ready.IsCompleted && timeout != TimeSpan.Zero)
        {
            try
            {
                await ready.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        return watch.Drain(_replyBudget);
    }

    /// <summary>
    /// Waits until a watch of <paramref name="owner"/> has changes, the owner adds a watch or <paramref name="timeout"/>
    /// passes, then returns the queued changes of every ready watch of the owner, as many as fit a reply. A watch whose
    /// changes do not all fit keeps the rest for the next poll.
    /// </summary>
    public async Task<HookWaitAnyChangesReply> WaitAnyAsync(long owner, TimeSpan timeout, CancellationToken cancellationToken)
    {
        OwnerSignal signal = GetSignal(owner);
        // Taken before the scan: a change that arrives after the scan completes it, so nothing is missed.
        Task woken = signal.Reset();
        List<HookWatchChanges> ready = Collect(owner, signal);
        if (ready.Count == 0 && timeout != TimeSpan.Zero)
        {
            try
            {
                await woken.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            ready = Collect(owner, signal);
        }
        return new HookWaitAnyChangesReply { Watches = ready };
    }

    /// <summary>
    /// The signal of a connected owner (kept until its last connection closes); an owner with no connection gets a
    /// throwaway one, so a call from a process that already went away leaves nothing behind.
    /// </summary>
    private OwnerSignal GetSignal(long owner)
    {
        lock (_connectionsLock)
            return _connections.ContainsKey(owner) ? _signals.GetOrAdd(owner, static _ => new OwnerSignal()) : new OwnerSignal();
    }

    /// <summary>Owners with a stored signal; tests check that nothing is left for owners that are gone.</summary>
    internal int SignalCount => _signals.Count;

    /// <summary>
    /// Drains the ready watches of <paramref name="owner"/> into one reply, round robin: the scan starts after the
    /// last watch the previous reply carried, so a watch that always has changes cannot keep the others out.
    /// </summary>
    private List<HookWatchChanges> Collect(long owner, OwnerSignal signal)
    {
        var ready = new List<HookWatchChanges>();
        int budget = _anyReplyBudget;
        KeyValuePair<uint, Watch>[] owned = _watches.Where(x => x.Value.Owner == owner).OrderBy(x => x.Key).ToArray();
        int start = Array.FindIndex(owned, x => x.Key > signal.LastCollected);
        if (start < 0)
            start = 0;
        for (int n = 0; n < owned.Length; n++)
        {
            KeyValuePair<uint, Watch> pair = owned[(start + n) % owned.Length];
            Watch watch = pair.Value;
            if (!watch.HasData || ready.Count == ushort.MaxValue)
                continue;
            if (budget < HookWatchChanges.FixedSize + MinChangeSize)
                break;
            // A change too big for what is left waits for the next poll; only a reply's first group may declare it lost.
            if (ready.Count > 0 && watch.FirstChangeSize > budget - HookWatchChanges.FixedSize)
                continue;
            HookWaitChangesReply drained = watch.Drain(budget - HookWatchChanges.FixedSize);
            if (drained.Changes.Count == 0 && !drained.Overflow)
                continue;
            ready.Add(new HookWatchChanges(pair.Key, drained.Changes, drained.Overflow));
            signal.LastCollected = pair.Key;
            budget -= HookWatchChanges.FixedSize + drained.Changes.Sum(x => x.EncodedSize);
        }
        return ready;
    }

    /// <summary>Smallest encoded change (action and an empty name): below this a group cannot carry anything.</summary>
    private const int MinChangeSize = 6;

    private void OnRemoteChanged(FsChangeNotification notification) => _notifications.Writer.TryWrite(notification);

    private async Task DispatchAsync()
    {
        await foreach (FsChangeNotification notification in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                BeforeDispatch?.Invoke();
                Apply(notification);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // One bad notification must not end the dispatching: the watches would silently go deaf.
                System.Diagnostics.Trace.TraceError($"ProcBridge: applying a change notification to the hook watches failed: {e.Message}");
            }
            finally
            {
                Interlocked.Increment(ref _dispatched);
            }
        }
    }

    private void Apply(FsChangeNotification notification)
    {
        foreach (Watch watch in _watches.Values)
        {
            if (notification.Rescan)
            {
                watch.MarkOverflow();
                continue;
            }
            foreach (FsChange change in notification.Changes)
            {
                if (watch.TryGetRelativeName(change.Path) is { } name)
                    watch.Enqueue(new HookChange((uint)change.Action, name));
            }
        }
    }

    private static ProtocolStatusException UnknownWatch(uint id) => new(NtStatus.InvalidHandle, $"Unknown watch {id}.");

    /// <summary>Wakes the WaitAnyChanges poll of one owner: set by any of its watches becoming ready and by a new watch.</summary>
    private sealed class OwnerSignal
    {
        private readonly object _lock = new();
        private TaskCompletionSource _woken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Id of the last watch a reply carried (one poll at a time per owner, so no lock).</summary>
        public uint LastCollected { get; set; }

        public void Set()
        {
            lock (_lock)
                _woken.TrySetResult();
        }

        /// <summary>Arms the signal for a new wait and returns the task that the next <see cref="Set"/> completes.</summary>
        public Task Reset()
        {
            lock (_lock)
            {
                if (_woken.Task.IsCompleted)
                    _woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _woken.Task;
            }
        }
    }

    private sealed class Watch(long owner, string directory, bool subtree, OwnerSignal signal)
    {
        public long Owner { get; } = owner;

        /// <summary>Encoded size of the oldest queued change, 0 when none (or when changes were lost).</summary>
        public int FirstChangeSize
        {
            get
            {
                lock (_lock)
                    return !_overflow && _queue.TryPeek(out HookChange change) ? change.EncodedSize : 0;
            }
        }

        /// <summary>Queued changes or lost changes to report.</summary>
        public bool HasData
        {
            get
            {
                lock (_lock)
                    return !_closed && (_overflow || _queue.Count > 0);
            }
        }

        private readonly string _prefix = directory.Trim('/') is { Length: > 0 } trimmed ? trimmed + "/" : string.Empty;
        private readonly object _lock = new();
        private readonly Queue<HookChange> _queue = new();
        private bool _overflow;
        private bool _closed;
        private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Name relative to the watched directory ('\' separated), or null when the path is outside it.</summary>
        public string? TryGetRelativeName(string path)
        {
            if (path.Length <= _prefix.Length || !path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            string relative = path[_prefix.Length..];
            if (!subtree && relative.Contains('/'))
                return null;
            return relative.Replace('/', '\\');
        }

        public void Enqueue(HookChange change)
        {
            lock (_lock)
            {
                if (_closed || _overflow)
                    return;
                if (_queue.Count >= MaxQueuedChanges)
                {
                    _queue.Clear();
                    _overflow = true;
                }
                else
                {
                    _queue.Enqueue(change);
                }
                _ready.TrySetResult();
            }
            signal.Set();
        }

        public void MarkOverflow()
        {
            lock (_lock)
            {
                if (_closed)
                    return;
                // The details are worthless once something was lost: the process re-lists the directory anyway.
                _queue.Clear();
                _overflow = true;
                _ready.TrySetResult();
            }
            signal.Set();
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                _queue.Clear();
                _ready.TrySetResult();
            }
        }

        public Task WhenReady()
        {
            lock (_lock)
                return _ready.Task;
        }

        public HookWaitChangesReply Drain(int budget)
        {
            lock (_lock)
            {
                if (_overflow)
                {
                    _overflow = false;
                    ResetReady();
                    return new HookWaitChangesReply { Changes = [], Overflow = true };
                }
                var changes = new List<HookChange>();
                while (_queue.TryPeek(out HookChange change) && changes.Count < ushort.MaxValue)
                {
                    if (change.EncodedSize > budget)
                    {
                        if (changes.Count > 0)
                            break;
                        // Not even the first change fits a reply: it can never be delivered, so tell the process to re-list.
                        _queue.Clear();
                        ResetReady();
                        return new HookWaitChangesReply { Changes = [], Overflow = true };
                    }
                    budget -= change.EncodedSize;
                    changes.Add(_queue.Dequeue());
                }
                ResetReady();
                return new HookWaitChangesReply { Changes = changes, Overflow = false };
            }
        }

        private void ResetReady()
        {
            if (_queue.Count == 0 && !_closed && _ready.Task.IsCompleted)
                _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
