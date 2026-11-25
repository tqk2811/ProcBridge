using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Watches one share with <c>ReadDirectoryChangesW</c> (through <see cref="FileSystemWatcher"/>) and hands batched
/// changes to every subscriber (plan section 10, M7). The watcher runs only while someone is subscribed. When events
/// are lost (the kernel buffer overflowed, the watcher failed, too many changes in one batch) subscribers get a
/// notification with <see cref="FsChangeNotification.Rescan"/> instead of a partial list.
/// Owns its <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class ShareWatcher : IDisposable
{
    /// <summary>A batch with more changes than this is replaced by a rescan: cheaper for both ends.</summary>
    public const int MaxChangesPerBatch = 4096;

    private readonly string _rootPath;
    private readonly int _bufferSize;
    private readonly TimeSpan _batchDelay;
    private readonly object _lock = new();
    // Snapshot and delivery of a batch happen under this, so subscribers see batches one at a time and in order.
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly List<Subscription> _subscribers = [];
    private List<FsChange> _batch = [];
    private bool _rescan;
    private bool _flushScheduled;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public ShareWatcher(string rootPath, int bufferSize, TimeSpan batchDelay)
    {
        _rootPath = rootPath;
        _bufferSize = bufferSize;
        _batchDelay = batchDelay;
    }

    public TimeSpan BatchDelay => _batchDelay;

    /// <summary>Number of rescans reported since start, for diagnostics and tests.</summary>
    public int RescanCount { get; private set; }

    /// <summary>
    /// Adds <paramref name="handler"/>; the watcher is running when this returns. The handler runs on a thread pool
    /// thread, one batch at a time per watcher, and must not block. Dispose the result to unsubscribe.
    /// </summary>
    public IDisposable Subscribe(Action<FsChangeNotification> handler)
    {
        var subscription = new Subscription(this, handler);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(subscription);
            _watcher ??= StartWatcher();
        }
        return subscription;
    }

    private FileSystemWatcher StartWatcher()
    {
        var watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = _bufferSize,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.CreationTime | NotifyFilters.Security,
        };
        watcher.Created += (_, e) => Add(FsChangeAction.Added, e.Name);
        watcher.Deleted += (_, e) => Add(FsChangeAction.Removed, e.Name);
        watcher.Changed += (_, e) => Add(FsChangeAction.Modified, e.Name);
        watcher.Renamed += (_, e) => AddRename(e.OldName, e.Name);
        watcher.Error += (_, e) => OnError(e.GetException());
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Unsubscribe(Subscription subscription)
    {
        FileSystemWatcher? stopped = null;
        lock (_lock)
        {
            _subscribers.Remove(subscription);
            if (_subscribers.Count == 0)
            {
                stopped = _watcher;
                _watcher = null;
                _batch = [];
                _rescan = false;
            }
        }
        stopped?.Dispose();
    }

    private void Add(FsChangeAction action, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        lock (_lock)
        {
            if (_watcher is null || _rescan)
                return;
            if (_batch.Count >= MaxChangesPerBatch)
            {
                MarkRescan();
                return;
            }
            _batch.Add(new FsChange { Action = action, Path = ToSharePath(name) });
            ScheduleFlush();
        }
    }

    private void AddRename(string? oldName, string? newName)
    {
        // A rename into or out of the share arrives with one side missing: it is then a plain add or remove.
        if (string.IsNullOrEmpty(oldName))
        {
            Add(FsChangeAction.Added, newName);
            return;
        }
        if (string.IsNullOrEmpty(newName))
        {
            Add(FsChangeAction.Removed, oldName);
            return;
        }
        lock (_lock)
        {
            if (_watcher is null || _rescan)
                return;
            if (_batch.Count + 2 > MaxChangesPerBatch)
            {
                MarkRescan();
                return;
            }
            _batch.Add(new FsChange { Action = FsChangeAction.RenamedOldName, Path = ToSharePath(oldName) });
            _batch.Add(new FsChange { Action = FsChangeAction.RenamedNewName, Path = ToSharePath(newName) });
            ScheduleFlush();
        }
    }

    /// <summary>
    /// Watcher error: a buffer overflow (<see cref="InternalBufferOverflowException"/>) loses events, any other error
    /// stops the watcher. Both become a rescan; a stopped watcher is restarted.
    /// </summary>
    internal void OnError(Exception exception)
    {
        FileSystemWatcher? broken = null;
        lock (_lock)
        {
            if (_watcher is null)
                return;
            if (exception is not InternalBufferOverflowException)
            {
                broken = _watcher;
                try
                {
                    _watcher = StartWatcher();
                }
                catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // Root gone or inaccessible: keep the dead watcher so subscribers stay counted; they learn of the
                    // problem from the rescan and the requests that follow.
                    broken = null;
                }
            }
            MarkRescan();
        }
        broken?.Dispose();
    }

    private void MarkRescan()
    {
        _rescan = true;
        _batch = [];
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled)
            return;
        _flushScheduled = true;
        _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(_batchDelay).ConfigureAwait(false);
        await _dispatch.WaitAsync().ConfigureAwait(false);
        try
        {
            FsChangeNotification notification;
            Subscription[] subscribers;
            lock (_lock)
            {
                _flushScheduled = false;
                if (!_rescan && _batch.Count == 0)
                    return;
                notification = new FsChangeNotification { Changes = _rescan ? [] : _batch.ToArray(), Rescan = _rescan };
                if (_rescan)
                    RescanCount++;
                _batch = [];
                _rescan = false;
                subscribers = _subscribers.ToArray();
            }
            foreach (Subscription subscriber in subscribers)
            {
                try
                {
                    subscriber.Handler(notification);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // One broken subscriber must not starve the others.
                }
            }
        }
        finally
        {
            _dispatch.Release();
        }
    }

    private static string ToSharePath(string name) => name.Replace('\\', '/').Trim('/');

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_lock)
        {
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _subscribers.Clear();
        }
        watcher?.Dispose();
    }

    private sealed class Subscription(ShareWatcher owner, Action<FsChangeNotification> handler) : IDisposable
    {
        private int _disposed;

        public Action<FsChangeNotification> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(this);
        }
    }
}
