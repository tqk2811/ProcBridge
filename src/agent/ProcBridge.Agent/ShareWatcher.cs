using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Watches one share with <c>ReadDirectoryChangesW</c> (through <see cref="FileSystemWatcher"/>) and hands batched
/// changes to every subscriber (plan section 10, M7). The watcher runs only while someone is subscribed. When events
/// are lost (the kernel buffer overflowed, the watcher failed, too many changes in one batch) subscribers get a
/// notification with <see cref="FsChangeNotification.Rescan"/> instead of a partial list. When the watcher cannot be
/// restarted (the root is gone or inaccessible) the subscribers are told it was lost (<c>onLost</c> of
/// <see cref="Subscribe"/>) and the restart is retried with growing delays; every successful retry sends a rescan.
/// Owns its <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class ShareWatcher : IDisposable
{
    /// <summary>A batch with more changes than this is replaced by a rescan: cheaper for both ends.</summary>
    public const int MaxChangesPerBatch = 4096;

    /// <summary>Longest wait between two attempts to restart a lost watcher.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly string _rootPath;
    private readonly int _bufferSize;
    private readonly TimeSpan _batchDelay;
    private readonly TimeSpan _retryDelay;
    private readonly object _lock = new();
    // Snapshot and delivery of a batch happen under this, so subscribers see batches one at a time and in order.
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly List<Subscription> _subscribers = [];
    private List<FsChange> _batch = [];
    private bool _rescan;
    private bool _flushScheduled;
    private FileSystemWatcher? _watcher;
    private bool _retrying;
    private bool _disposed;

    /// <param name="retryDelay">First wait before restarting a lost watcher (doubles up to <see cref="MaxRetryDelay"/>); default 1 s.</param>
    public ShareWatcher(string rootPath, int bufferSize, TimeSpan batchDelay, TimeSpan? retryDelay = null)
    {
        _rootPath = rootPath;
        _bufferSize = bufferSize;
        _batchDelay = batchDelay;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    }

    public TimeSpan BatchDelay => _batchDelay;

    /// <summary>Number of rescans reported since start, for diagnostics and tests.</summary>
    public int RescanCount { get; private set; }

    /// <summary>
    /// Adds <paramref name="handler"/>; the watcher is running when this returns. The handler runs on a thread pool
    /// thread, one batch at a time per watcher, and must not block. Dispose the result to unsubscribe.
    /// </summary>
    /// <param name="onLost">
    /// Called (on a thread pool thread) when the watcher broke and could not be restarted, so no changes are reported
    /// until a retry succeeds. The subscriber should end its subscription so its client falls back to short-lived caching.
    /// </param>
    /// <exception cref="IOException">The watcher cannot be started now; the subscriber is not added.</exception>
    public IDisposable Subscribe(Action<FsChangeNotification> handler, Action? onLost = null)
    {
        var subscription = new Subscription(this, handler, onLost);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _watcher ??= StartWatcher();
            }
            catch (Exception e) when (IsStartFailure(e) && e is not IOException)
            {
                throw new IOException($"Cannot watch '{_rootPath}': {e.Message}", e);
            }
            _subscribers.Add(subscription);
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
        try
        {
            watcher.Created += (_, e) => Add(FsChangeAction.Added, e.Name);
            watcher.Deleted += (_, e) => Add(FsChangeAction.Removed, e.Name);
            watcher.Changed += (_, e) => Add(FsChangeAction.Modified, e.Name);
            watcher.Renamed += (_, e) => AddRename(e.OldName, e.Name);
            watcher.Error += (_, e) => OnError(e.GetException());
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        return watcher;
    }

    private static bool IsStartFailure(Exception e) => e is IOException or ArgumentException or UnauthorizedAccessException;

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
    /// stops the watcher. Both become a rescan; a stopped watcher is restarted, and when that fails the subscribers
    /// are told (<c>onLost</c>) and the restart is retried in the background.
    /// </summary>
    internal void OnError(Exception exception)
    {
        FileSystemWatcher? broken = null;
        Action?[] lost = [];
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
                catch (Exception e) when (IsStartFailure(e))
                {
                    // Root gone or inaccessible: nothing is watched until a retry works.
                    _watcher = null;
                    lost = _subscribers.Select(x => x.OnLost).ToArray();
                    BeginRetry();
                }
            }
            MarkRescan();
        }
        broken?.Dispose();
        foreach (Action? onLost in lost)
        {
            try
            {
                onLost?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The callback of one subscriber must not keep the others from being told.
            }
        }
    }

    /// <summary>Starts the background restart of a lost watcher. Call with <see cref="_lock"/> held.</summary>
    private void BeginRetry()
    {
        if (_retrying)
            return;
        _retrying = true;
        _ = RetryAsync();
    }

    private async Task RetryAsync()
    {
        TimeSpan delay = _retryDelay;
        while (true)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            lock (_lock)
            {
                // Someone else restarted it (a new subscriber), or nobody is left to tell.
                if (_disposed || _watcher is not null || _subscribers.Count == 0)
                {
                    _retrying = false;
                    return;
                }
                try
                {
                    _watcher = StartWatcher();
                    _retrying = false;
                    // Whatever happened while nothing was watched is unknown: the subscribers must re-list.
                    MarkRescan();
                    return;
                }
                catch (Exception e) when (IsStartFailure(e))
                {
                }
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
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

    private sealed class Subscription(ShareWatcher owner, Action<FsChangeNotification> handler, Action? onLost) : IDisposable
    {
        private int _disposed;

        public Action<FsChangeNotification> Handler { get; } = handler;

        public Action? OnLost { get; } = onLost;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(this);
        }
    }
}
