using System.Collections.Concurrent;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Directory watches the hook registered for NtNotifyChangeDirectoryFile[Ex] (plan section 10, M7). Each watch
/// queues the changes of <see cref="FileCache.RemoteChanged"/> that fall inside its directory until the hook collects
/// them with a WaitChanges long poll. A rescan, a reconnect of the change watcher or a full queue marks the watch as
/// overflowed, which the hook turns into <c>STATUS_NOTIFY_ENUM_DIR</c>.
/// Every watch belongs to the client process that registered it (the owner, see <see cref="Attach"/>): only that
/// process can wait on or remove it, and the watches of a process go away when its last pipe connection closes.
/// Does not own the cache; subscribes to it for its whole lifetime.
/// </summary>
public sealed class HookWatchRegistry
{
    /// <summary>Changes kept per watch; beyond that the watch overflows (like a full notify buffer).</summary>
    public const int MaxQueuedChanges = 1024;

    /// <summary>Watches one session may hold; protects the Broker from a process that never closes handles.</summary>
    public const int MaxWatches = 4096;

    private readonly ConcurrentDictionary<uint, Watch> _watches = new();
    private readonly Dictionary<long, int> _connections = [];
    private readonly object _connectionsLock = new();
    private int _nextId;

    private readonly int _replyBudget;

    /// <param name="replyBudget">Bytes of changes one WaitChanges reply may carry; tests shrink it.</param>
    public HookWatchRegistry(FileCache cache, int replyBudget = DefaultReplyBudget)
    {
        _replyBudget = replyBudget;
        cache.RemoteChanged += OnRemoteChanged;
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
        _watches[id] = new Watch(owner, remoteDirectory, subtree);
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

    private void OnRemoteChanged(FsChangeNotification notification)
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

    private sealed class Watch(long owner, string directory, bool subtree)
    {
        public long Owner { get; } = owner;

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
