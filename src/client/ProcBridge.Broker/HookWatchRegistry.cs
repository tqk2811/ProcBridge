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
/// Does not own the cache; subscribes to it for its whole lifetime.
/// </summary>
public sealed class HookWatchRegistry
{
    /// <summary>Changes kept per watch; beyond that the watch overflows (like a full notify buffer).</summary>
    public const int MaxQueuedChanges = 1024;

    /// <summary>Watches one session may hold; protects the Broker from a process that never closes handles.</summary>
    public const int MaxWatches = 4096;

    private readonly ConcurrentDictionary<uint, Watch> _watches = new();
    private int _nextId;

    public HookWatchRegistry(FileCache cache)
    {
        cache.RemoteChanged += OnRemoteChanged;
    }

    public int Count => _watches.Count;

    /// <summary>Starts watching the share directory <paramref name="remoteDirectory"/>; returns the watch id (never 0).</summary>
    public uint Add(string remoteDirectory, bool subtree)
    {
        if (_watches.Count >= MaxWatches)
            throw new ProtocolStatusException(NtStatus.InsufficientResources, "Too many directory watches.");
        uint id;
        do
            id = (uint)Interlocked.Increment(ref _nextId);
        while (id == 0);
        _watches[id] = new Watch(remoteDirectory, subtree);
        return id;
    }

    /// <summary>Removes the watch; a WaitChanges blocked on it returns empty.</summary>
    public void Remove(uint id)
    {
        if (!_watches.TryRemove(id, out Watch? watch))
            throw UnknownWatch(id);
        watch.Close();
    }

    /// <summary>
    /// Waits until watch <paramref name="id"/> has changes or <paramref name="timeout"/> passes, then returns as many
    /// queued changes as fit a reply. <see cref="Timeout.InfiniteTimeSpan"/> waits until a change or removal.
    /// </summary>
    public async Task<HookWaitChangesReply> WaitAsync(uint id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_watches.TryGetValue(id, out Watch? watch))
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
        return watch.Drain(HookIpcProtocol.MaxMessageSize - HookIpcProtocol.HeaderSize - HookWaitChangesReply.FixedSize);
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

    private sealed class Watch(string directory, bool subtree)
    {
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
                        break;
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
