using System.Collections.Concurrent;
using ProcBridge.Broker.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker;

/// <summary>
/// Local cache of one share (plan 4.4 and 10). Stat and list results live for a short TTL, or until a change
/// notification invalidates them while a <see cref="ChangeWatcher"/> is attached and active; file contents are
/// downloaded whole, get machine B's times and attributes, and are reused while the remote version
/// (last write time + size) stays the same. Concurrent requests for the same file share one download.
/// Does not own the <see cref="AgentClient"/>.
/// </summary>
public sealed class FileCache
{
    private const int MaxVersionRetries = 2;

    /// <summary>Attributes copied onto cache files; the others (directory, sparse, reparse...) describe storage.</summary>
    private const FileAttributes MirroredAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed;

    private readonly AgentClient _client;
    private readonly string _cacheRoot;
    private readonly FileCacheOptions _options;
    private readonly ConcurrentDictionary<string, Timed<FsEntry>> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timed<IReadOnlyList<FsEntry>>> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FsVersion> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedItem>>> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingStat> _pendingStats = new(StringComparer.OrdinalIgnoreCase);
    // Bumped by every invalidation; a fetch that started under an older value may hold stale data and gets the short TTL.
    private long _epoch;
    private volatile bool _watchActive;
    private readonly ConcurrentDictionary<string, byte> _readAheadDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _readAheadTasks = new();
    private int _readAheadDownloads;
    // Caps the read-ahead downloads of ALL directories together, so a build opening files everywhere cannot take the whole pool.
    private readonly SemaphoreSlim _readAheadGate;
    private int _readAheadRunning;
    private int _readAheadPeak;

    /// <summary>Test hook, called after a fetch computed its expiry and before it stores the result; lets a test invalidate in that gap.</summary>
    internal Action? BeforeMetadataStore { get; set; }

    public FileCache(AgentClient client, string cacheRoot, TimeSpan metadataTtl)
        : this(client, cacheRoot, new FileCacheOptions { MetadataTtl = metadataTtl })
    {
    }

    public FileCache(AgentClient client, string cacheRoot, FileCacheOptions options)
    {
        _client = client;
        _cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        _options = options;
        _readAheadGate = new SemaphoreSlim(Math.Max(1, options.ReadAheadParallelism));
        Directory.CreateDirectory(_cacheRoot);
        DeleteLeftoverTempFiles();
    }

    /// <summary>Removes <c>*.pbdl-*.tmp</c> files that a crashed Broker left behind in earlier runs.</summary>
    private void DeleteLeftoverTempFiles()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(_cacheRoot, "*.pbdl-*.tmp", options))
            TryDelete(path);
    }

    public string CacheRoot => _cacheRoot;

    public async Task<FsEntry> StatAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (_stats.TryGetValue(remotePath, out Timed<FsEntry>? cached) && !cached.IsExpired)
            return cached.Value;

        // Build tools stat the same file from many threads at once: share one request per path. A request that
        // started before an invalidation may return the old state, so it is only shared with callers of the same epoch.
        long epoch = Volatile.Read(ref _epoch);
        PendingStat pending = _pendingStats.GetOrAdd(remotePath, key => new PendingStat(epoch, new Lazy<Task<FsEntry>>(() => FetchStatAsync(key))));
        while (pending.Epoch != epoch)
        {
            var fresh = new PendingStat(epoch, new Lazy<Task<FsEntry>>(() => FetchStatAsync(remotePath)));
            if (_pendingStats.TryUpdate(remotePath, fresh, pending))
            {
                pending = fresh;
                break;
            }
            pending = _pendingStats.GetOrAdd(remotePath, fresh);
        }
        try
        {
            return await pending.Fetch.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.Fetch.IsValueCreated && pending.Fetch.Value.IsCompleted)
                _pendingStats.TryRemove(new KeyValuePair<string, PendingStat>(remotePath, pending));
        }
    }

    private async Task<FsEntry> FetchStatAsync(string remotePath)
    {
        await Task.Yield();
        long epoch = Volatile.Read(ref _epoch);
        FsEntry entry = await _client.StatAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
        long expires = ExpiryFor(epoch);
        BeforeMetadataStore?.Invoke();
        StoreStat(remotePath, entry, expires, epoch);
        return entry;
    }

    public async Task<IReadOnlyList<FsEntry>> ListAsync(string remotePath, CancellationToken cancellationToken)
    {
        if (_lists.TryGetValue(remotePath, out Timed<IReadOnlyList<FsEntry>>? cached) && !cached.IsExpired)
            return cached.Value;
        long epoch = Volatile.Read(ref _epoch);
        IReadOnlyList<FsEntry> entries = await _client.ListAsync(remotePath, cancellationToken).ConfigureAwait(false);
        long expires = ExpiryFor(epoch);
        BeforeMetadataStore?.Invoke();
        var list = new Timed<IReadOnlyList<FsEntry>>(entries, expires);
        _lists[remotePath] = list;
        // A listing also refreshes the children's metadata, saving one stat per file for build tools.
        string prefix = remotePath.Length == 0 ? string.Empty : remotePath + "/";
        foreach (FsEntry entry in entries)
            _stats[prefix + entry.Name] = new Timed<FsEntry>(entry, expires);
        if (Volatile.Read(ref _epoch) != epoch)
        {
            // An invalidation ran while the data was in flight and may have missed it: take it back out.
            _lists.TryRemove(new KeyValuePair<string, Timed<IReadOnlyList<FsEntry>>>(remotePath, list));
            foreach (FsEntry entry in entries)
                _stats.TryRemove(new KeyValuePair<string, Timed<FsEntry>>(prefix + entry.Name, new Timed<FsEntry>(entry, expires)));
        }
        return entries;
    }

    /// <summary>
    /// Stores a fetched stat. Invalidation bumps the epoch before it removes keys, so re-reading the epoch after the
    /// write catches an invalidation that ran in between and missed this entry (it is then removed again).
    /// </summary>
    private void StoreStat(string remotePath, FsEntry entry, long expires, long epochAtStart)
    {
        var timed = new Timed<FsEntry>(entry, expires);
        _stats[remotePath] = timed;
        if (Volatile.Read(ref _epoch) != epochAtStart)
            _stats.TryRemove(new KeyValuePair<string, Timed<FsEntry>>(remotePath, timed));
    }

    /// <summary>True while a change watcher is subscribed: metadata then lives until a notification invalidates it.</summary>
    public bool IsWatchActive => _watchActive;

    /// <summary>
    /// Raised after the cache applied a change notification. A notification with
    /// <see cref="FsChangeNotification.Rescan"/> is also raised when the watcher connects or disconnects, since changes
    /// may have been missed in between.
    /// </summary>
    public event Action<FsChangeNotification>? RemoteChanged;

    /// <summary>
    /// Lets <paramref name="watcher"/> drive invalidation. Call before starting the watcher. Does not take ownership.
    /// </summary>
    public void AttachWatcher(ChangeWatcher watcher)
    {
        watcher.StateChanged += OnWatchStateChanged;
        watcher.Changed += OnRemoteChanged;
    }

    private void OnWatchStateChanged(bool active)
    {
        // Either way changes may have been missed: before the subscription, or from now on.
        _watchActive = active;
        ClearMetadata();
        RemoteChanged?.Invoke(new FsChangeNotification { Changes = [], Rescan = true });
    }

    private void OnRemoteChanged(FsChangeNotification notification)
    {
        if (notification.Rescan)
        {
            ClearMetadata();
        }
        else
        {
            foreach (FsChange change in notification.Changes)
                Invalidate(change);
        }
        RemoteChanged?.Invoke(notification);
    }

    /// <summary>Forgets every stat and listing (contents stay: they are checked by version before reuse).</summary>
    public void ClearMetadata()
    {
        Interlocked.Increment(ref _epoch);
        _stats.Clear();
        _lists.Clear();
        _readAheadDirectories.Clear();
    }

    /// <summary>Drops what <paramref name="change"/> makes stale: the path, its parent's listing and, when it went away, its subtree.</summary>
    private void Invalidate(FsChange change)
    {
        Interlocked.Increment(ref _epoch);
        string path = change.Path;
        string parent = GetParent(path);
        _stats.TryRemove(path, out _);
        _lists.TryRemove(path, out _);
        _lists.TryRemove(parent, out _);
        _readAheadDirectories.TryRemove(parent, out _);
        // The parent's write time changes with its children.
        _stats.TryRemove(parent, out _);
        if (change.Action is FsChangeAction.Removed or FsChangeAction.RenamedOldName or FsChangeAction.RenamedNewName or FsChangeAction.Added)
        {
            // A directory that vanished (or appeared in place of something) takes its cached subtree with it.
            string prefix = path + "/";
            RemoveByPrefix(_stats, prefix);
            RemoveByPrefix(_lists, prefix);
        }
        if (change.Action is FsChangeAction.Removed or FsChangeAction.RenamedOldName)
        {
            _versions.TryRemove(path, out _);
            RemoveByPrefix(_versions, path + "/");
        }
    }

    private static void RemoveByPrefix<T>(ConcurrentDictionary<string, T> map, string prefix)
    {
        foreach (string key in map.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                map.TryRemove(key, out _);
        }
    }

    private static string GetParent(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>Expiry for metadata fetched when the epoch was <paramref name="epochAtStart"/>.</summary>
    private long ExpiryFor(long epochAtStart)
    {
        bool trusted = _watchActive && Volatile.Read(ref _epoch) == epochAtStart;
        return Environment.TickCount64 + (long)(trusted ? _options.WatchedMetadataTtl : _options.MetadataTtl).TotalMilliseconds;
    }

    /// <summary>
    /// Loads the stats and listings of the whole subtree at <paramref name="remotePath"/> with one tree walk
    /// (plan section 11), so a build that touches thousands of files needs no round trip per directory.
    /// Directories past the depth or the Agent's entry limit are left to on-demand listing.
    /// </summary>
    /// <returns>Number of entries loaded.</returns>
    public async Task<int> PreloadTreeAsync(string remotePath, int maxDepth, CancellationToken cancellationToken)
    {
        long epoch = Volatile.Read(ref _epoch);
        var partial = new Dictionary<string, List<FsEntry>>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        bool stale = false;
        await _client.WalkTreeAsync(remotePath, maxDepth, page =>
        {
            // Once an invalidation ran, the rest of the walk may be older than it: stop caching, the data is read on demand.
            if (stale)
                return;
            long expires = ExpiryFor(epoch);
            BeforeMetadataStore?.Invoke();
            foreach (FsTreeDirectory directory in page.Directories)
            {
                string prefix = directory.Path.Length == 0 ? string.Empty : directory.Path + "/";
                int stored = 0;
                foreach (FsEntry entry in directory.Entries)
                {
                    if (Volatile.Read(ref _epoch) != epoch)
                        break;
                    _stats[prefix + entry.Name] = new Timed<FsEntry>(entry, expires);
                    stored++;
                }
                count += stored;

                if (!partial.TryGetValue(directory.Path, out List<FsEntry>? list))
                    partial[directory.Path] = list = [];
                list.AddRange(directory.Entries);
                Timed<IReadOnlyList<FsEntry>>? storedList = null;
                if (directory.Complete && stored == directory.Entries.Length)
                {
                    storedList = new Timed<IReadOnlyList<FsEntry>>(list.ToArray(), expires);
                    _lists[directory.Path] = storedList;
                    partial.Remove(directory.Path);
                }

                // An invalidation after the last check would have missed what was just written: take it back out.
                if (stored < directory.Entries.Length || Volatile.Read(ref _epoch) != epoch)
                {
                    stale = true;
                    for (int i = 0; i < stored; i++)
                        _stats.TryRemove(new KeyValuePair<string, Timed<FsEntry>>(prefix + directory.Entries[i].Name, new Timed<FsEntry>(directory.Entries[i], expires)));
                    if (storedList is not null)
                        _lists.TryRemove(new KeyValuePair<string, Timed<IReadOnlyList<FsEntry>>>(directory.Path, storedList));
                    return;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>Makes sure <paramref name="remotePath"/> is in the cache with the current remote version.</summary>
    /// <remarks>Opening a small file starts the read-ahead of its directory (<see cref="FileCacheOptions.ReadAheadMaxFileSize"/>).</remarks>
    public Task<CachedItem> EnsureAsync(string remotePath, CancellationToken cancellationToken)
        => EnsureAsync(remotePath, readAhead: true, cancellationToken);

    /// <summary>Files downloaded by read-ahead so far, for diagnostics, tests and benchmarks.</summary>
    public int ReadAheadDownloads => Volatile.Read(ref _readAheadDownloads);

    /// <summary>Most read-ahead downloads that ran at the same time, for tests.</summary>
    internal int ReadAheadPeakConcurrency => Volatile.Read(ref _readAheadPeak);

    /// <summary>Completes when every read-ahead started so far has finished.</summary>
    public Task WaitForReadAheadAsync() => Task.WhenAll(_readAheadTasks.Keys);

    private async Task<CachedItem> EnsureAsync(string remotePath, bool readAhead, CancellationToken cancellationToken)
    {
        FsEntry entry = await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
        string localPath = GetLocalPath(remotePath);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
            return new CachedItem(entry, localPath);
        }

        if (readAhead && _options.ReadAheadMaxFileSize > 0 && entry.Size <= _options.ReadAheadMaxFileSize)
            StartReadAhead(GetParent(remotePath), remotePath);

        if (_versions.TryGetValue(remotePath, out FsVersion version) && version == entry.Version && MatchesVersion(localPath, version))
            return new CachedItem(entry, localPath);

        Lazy<Task<CachedItem>> download = _downloads.GetOrAdd(remotePath,
            key => new Lazy<Task<CachedItem>>(() => DownloadAsync(key, entry, localPath, CancellationToken.None)));
        try
        {
            // The shared download is not cancelled by one waiter giving up.
            return await download.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (download.IsValueCreated && download.Value.IsCompleted)
                _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<CachedItem>>>(remotePath, download));
        }
    }

    /// <summary>Downloads the small files of <paramref name="remoteDirectory"/> in the background, once per directory until it changes.</summary>
    /// <param name="opened">The file whose open triggered it; the foreground request downloads that one.</param>
    private void StartReadAhead(string remoteDirectory, string opened)
    {
        if (!_readAheadDirectories.TryAdd(remoteDirectory, 0))
            return;
        Task task = Task.Run(() => ReadAheadAsync(remoteDirectory, opened));
        _readAheadTasks.TryAdd(task, 0);
        task.ContinueWith(x => _readAheadTasks.TryRemove(x, out _), TaskScheduler.Default);
    }

    private async Task ReadAheadAsync(string remoteDirectory, string opened)
    {
        IReadOnlyList<FsEntry> entries;
        try
        {
            entries = await ListAsync(remoteDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Read-ahead is a guess; the foreground request reports real errors.
            return;
        }

        string prefix = remoteDirectory.Length == 0 ? string.Empty : remoteDirectory + "/";
        string[] candidates = entries
            .Where(x => !x.IsDirectory && x.Size <= _options.ReadAheadMaxFileSize)
            .Select(x => prefix + x.Name)
            .Where(x => !x.Equals(opened, StringComparison.OrdinalIgnoreCase))
            .Where(x => !(_versions.TryGetValue(x, out FsVersion version) && MatchesVersion(GetLocalPath(x), version)))
            .Take(_options.ReadAheadMaxFiles)
            .ToArray();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.ReadAheadParallelism) };
        await Parallel.ForEachAsync(candidates, parallel, async (path, token) =>
        {
            await _readAheadGate.WaitAsync(token).ConfigureAwait(false);
            int running = Interlocked.Increment(ref _readAheadRunning);
            int peak;
            while (running > (peak = Volatile.Read(ref _readAheadPeak)) && Interlocked.CompareExchange(ref _readAheadPeak, running, peak) != peak)
            {
            }
            try
            {
                CachedItem item = await EnsureAsync(path, readAhead: false, token).ConfigureAwait(false);
                // The file may have grown since it was listed; it is downloaded by then, but count only real read-ahead.
                if (item.Entry.Size <= _options.ReadAheadMaxFileSize)
                    Interlocked.Increment(ref _readAheadDownloads);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _readAheadRunning);
                _readAheadGate.Release();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>True when the cache file exists and still has the size and write time of the downloaded version.</summary>
    private static bool MatchesVersion(string localPath, FsVersion version)
    {
        var info = new FileInfo(localPath);
        return info.Exists && info.Length == version.Size && info.LastWriteTimeUtc.ToFileTimeUtc() == version.LastWriteTime;
    }

    /// <summary>Local path a remote path is cached at.</summary>
    public string GetLocalPath(string remotePath)
        => remotePath.Length == 0 ? _cacheRoot : Path.Join(_cacheRoot, remotePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<CachedItem> DownloadAsync(string remotePath, FsEntry entry, string localPath, CancellationToken cancellationToken)
    {
        // Leave the GetOrAdd factory before doing I/O. The entry in _downloads is removed by EnsureAsync.
        await Task.Yield();
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        for (int attempt = 0; ; attempt++)
        {
            string temp = $"{localPath}.pbdl-{Guid.NewGuid():N}.tmp";
            try
            {
                await _client.DownloadAsync(remotePath, entry, temp, cancellationToken).ConfigureAwait(false);
                ApplyMetadata(temp, entry);
                if (File.Exists(localPath))
                    File.SetAttributes(localPath, FileAttributes.Normal);
                File.Move(temp, localPath, overwrite: true);
                // The share is read only, so the cached copy is too, whatever machine B says.
                File.SetAttributes(localPath, Mirrored(entry.Attributes) | FileAttributes.ReadOnly);
                _versions[remotePath] = entry.Version;
                return new CachedItem(entry, localPath);
            }
            catch (ProtocolStatusException e) when (e.Status == NtStatus.FileVersionChanged && attempt < MaxVersionRetries)
            {
                _stats.TryRemove(remotePath, out _);
                entry = await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
                if (entry.IsDirectory)
                    throw new ProtocolStatusException(NtStatus.FileIsADirectory, "Remote file became a directory.");
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    private static void ApplyMetadata(string path, FsEntry entry)
    {
        File.SetCreationTimeUtc(path, DateTime.FromFileTimeUtc(entry.CreationTime));
        File.SetLastWriteTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastWriteTime));
        File.SetLastAccessTimeUtc(path, DateTime.FromFileTimeUtc(entry.LastAccessTime));
    }

    private static FileAttributes Mirrored(uint attributes) => (FileAttributes)attributes & MirroredAttributes;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A stat request in flight and the epoch its callers saw when they started it.</summary>
    private sealed class PendingStat(long epoch, Lazy<Task<FsEntry>> fetch)
    {
        public long Epoch { get; } = epoch;

        public Lazy<Task<FsEntry>> Fetch { get; } = fetch;
    }

    private sealed record Timed<T>(T Value, long ExpiresAt)
    {
        public bool IsExpired => Environment.TickCount64 >= ExpiresAt;
    }
}
