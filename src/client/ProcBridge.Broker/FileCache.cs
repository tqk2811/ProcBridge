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
/// On a writable share (M3) cache copies keep machine B's attributes (a read-only share forces read-only), and the
/// write operations below change machine B first, then bring the cache and its metadata in line.
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
    // M8: files cached by ranges, by share path (the current copy) and by id (every copy a hook may still read).
    private readonly ConcurrentDictionary<string, RangeFile> _ranges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, RangeFile> _rangesById = new();
    private readonly object _rangeLock = new();
    private uint _nextRangeId;
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
    private Lazy<Task<bool>> _writable;
    private Func<string, bool>? _isPinned;

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
        _writable = new Lazy<Task<bool>>(() => client.IsWritableAsync(CancellationToken.None));
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

    /// <summary>True when the share accepts writes; asked once per Broker (Hello answer).</summary>
    public async Task<bool> IsWritableAsync(CancellationToken cancellationToken)
    {
        Lazy<Task<bool>> current = _writable;
        try
        {
            return await current.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (current.Value.IsFaulted)
        {
            // A failed first connect must not stick: ask again next time.
            Interlocked.CompareExchange(ref _writable, new Lazy<Task<bool>>(() => _client.IsWritableAsync(CancellationToken.None)), current);
            throw;
        }
    }

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
            // Gone from that name on machine B: its missing blocks cannot be fetched any more, close the write handle.
            if (_ranges.TryRemove(path, out RangeFile? range))
                range.Abandon();
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
        => EnsureAsync(remotePath, readAhead: true, allowRanges: false, cancellationToken);

    /// <summary>
    /// Like <see cref="EnsureAsync(string, CancellationToken)"/>; with <paramref name="allowRanges"/> a file above
    /// <see cref="FileCacheOptions.RangeThreshold"/> may come back as a sparse copy (<see cref="CachedItem.RangeId"/>)
    /// instead of being downloaded whole. Without it a ranged copy of the current version is completed in place.
    /// </summary>
    public Task<CachedItem> EnsureAsync(string remotePath, bool allowRanges, CancellationToken cancellationToken)
        => EnsureAsync(remotePath, readAhead: true, allowRanges, cancellationToken);

    /// <summary>
    /// Like <see cref="EnsureAsync(string, bool, CancellationToken)"/> with the stat the caller just got from machine B:
    /// no second stat, even when an invalidation elsewhere kept <paramref name="current"/> out of the cache.
    /// </summary>
    public Task<CachedItem> EnsureAsync(string remotePath, FsEntry current, bool allowRanges, CancellationToken cancellationToken)
        => EnsureAsync(remotePath, readAhead: true, allowRanges, cancellationToken, current);

    /// <summary>Bytes downloaded into ranged copies so far (M8), for tests and measurements.</summary>
    public long RangeBytesDownloaded => _rangesById.Values.Sum(x => x.BytesDownloaded);

    /// <summary>
    /// Brings [<paramref name="offset"/>, +<paramref name="length"/>) of ranged copy <paramref name="rangeId"/> in, or the
    /// whole file with <paramref name="whole"/>. Returns true when the copy is complete.
    /// </summary>
    public async Task<bool> FetchRangeAsync(uint rangeId, long offset, long length, bool whole, CancellationToken cancellationToken)
    {
        if (!_rangesById.TryGetValue(rangeId, out RangeFile? range))
            throw new ProtocolStatusException(NtStatus.InvalidParameter, $"Unknown range id {rangeId}.");
        if (whole)
            await range.CompleteAsync(_options.RangeParallelism, cancellationToken).ConfigureAwait(false);
        else
            await range.EnsureAsync(offset, length, cancellationToken).ConfigureAwait(false);
        return range.IsComplete;
    }

    /// <summary>Starts a sparse copy of <paramref name="entry"/> (or joins the current one of the same version).</summary>
    private async Task<CachedItem> StartRangeAsync(string remotePath, FsEntry entry, string localPath, CancellationToken cancellationToken)
    {
        bool writable = await IsWritableAsync(cancellationToken).ConfigureAwait(false);
        lock (_rangeLock)
        {
            if (_ranges.TryGetValue(remotePath, out RangeFile? current) && current.Version == entry.Version && !current.IsComplete)
                return new CachedItem(entry, current.LocalPath, current.Id);
            // An unfinished copy of an older version: machine B no longer serves its missing blocks (version check), so
            // its write handle goes; the blocks it has stay readable through the handles that have it open.
            current?.Abandon();
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            // An older copy stays readable through the handles that have it open (renamed away, its RangeFile keeps
            // serving their ranges); the name is free for the new one.
            RemoveLocal(localPath);
            _versions.TryRemove(remotePath, out _);
            uint id = ++_nextRangeId;
            RangeFile range = RangeFile.Create(id, remotePath, entry, localPath, _client, _options.RangeBlockSize, _options.RangePrefetchBlocks);
            range.OnComplete = OnRangeComplete;
            _rangesById[id] = range;
            if (!range.IsComplete)
                _ranges[remotePath] = range;
            else
                _versions[remotePath] = entry.Version;
            FileAttributes attributes = Mirrored(entry.Attributes) | (writable ? 0 : FileAttributes.ReadOnly);
            File.SetAttributes(localPath, attributes != 0 ? attributes : FileAttributes.Normal);
            return new CachedItem(entry, localPath, range.IsComplete ? 0 : id);
        }
    }

    /// <summary>Closes the write handles of unfinished ranged copies (Broker shutdown); their hooks then fail reads.</summary>
    public void AbandonRanges()
    {
        foreach (RangeFile range in _rangesById.Values)
            range.Abandon();
    }

    private void OnRangeComplete(RangeFile range)
    {
        // The copy holds exactly that version now, like a whole download.
        if (_ranges.TryRemove(new KeyValuePair<string, RangeFile>(range.RemotePath, range)))
            _versions[range.RemotePath] = range.Version;
    }

    /// <summary>Files downloaded by read-ahead so far, for diagnostics, tests and benchmarks.</summary>
    public int ReadAheadDownloads => Volatile.Read(ref _readAheadDownloads);

    /// <summary>Most read-ahead downloads that ran at the same time, for tests.</summary>
    internal int ReadAheadPeakConcurrency => Volatile.Read(ref _readAheadPeak);

    /// <summary>Completes when every read-ahead started so far has finished.</summary>
    public Task WaitForReadAheadAsync() => Task.WhenAll(_readAheadTasks.Keys);

    private async Task<CachedItem> EnsureAsync(string remotePath, bool readAhead, bool allowRanges, CancellationToken cancellationToken, FsEntry? current = null)
    {
        FsEntry entry = current ?? await StatAsync(remotePath, cancellationToken).ConfigureAwait(false);
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
        // A writer's unsent changes win over machine B's newer version until its write-back decides (conflict policy).
        if (IsPinned(remotePath) && File.Exists(localPath))
            return new CachedItem(entry, localPath);

        if (_ranges.TryGetValue(remotePath, out RangeFile? range) && range.Version == entry.Version)
        {
            if (allowRanges && !range.IsComplete)
                return new CachedItem(entry, range.LocalPath, range.Id);
            // A writer or a whole-file reader: complete the sparse copy in place (readers keep their handles).
            await range.CompleteAsync(_options.RangeParallelism, cancellationToken).ConfigureAwait(false);
            return new CachedItem(entry, range.LocalPath);
        }
        if (allowRanges && _options.RangeThreshold > 0 && entry.Size > _options.RangeThreshold)
            return await StartRangeAsync(remotePath, entry, localPath, cancellationToken).ConfigureAwait(false);

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
                CachedItem item = await EnsureAsync(path, readAhead: false, allowRanges: false, token).ConfigureAwait(false);
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
                if (IsPinned(remotePath) && File.Exists(localPath))
                    return new CachedItem(entry, localPath); // a writer opened it meanwhile: keep its copy
                // The old copy may be mapped as an image (a process started from the virtual folder runs from it): it cannot
                // be overwritten, but it can be renamed away, so the new copy takes the name and the running process keeps the old.
                RemoveLocal(localPath);
                if (File.Exists(localPath))
                    File.SetAttributes(localPath, FileAttributes.Normal);
                File.Move(temp, localPath, overwrite: true);
                // A read-only share makes the cached copy read only, whatever machine B says.
                bool writable = await IsWritableAsync(cancellationToken).ConfigureAwait(false);
                File.SetAttributes(localPath, Mirrored(entry.Attributes) | (writable ? 0 : FileAttributes.ReadOnly));
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

    /// <summary>
    /// Marks cache copies that must never be replaced by a download: <paramref name="isPinned"/> returns true while a
    /// writer holds changes machine B does not have yet (<see cref="WriteBackQueue.IsBusy"/>). A change notification or a
    /// read-ahead then still refreshes metadata but leaves the dirty copy alone.
    /// </summary>
    public void SetPinnedCheck(Func<string, bool> isPinned) => _isPinned = isPinned;

    private bool IsPinned(string remotePath) => _isPinned?.Invoke(remotePath) == true;

    /// <summary>Forgets stat and list results of <paramref name="remotePath"/>, its parent's listing and stat.</summary>
    public void Invalidate(string remotePath)
    {
        // Bump first, like Invalidate(FsChange): a fetch in flight that read the old state must not store it after this.
        Interlocked.Increment(ref _epoch);
        string parent = GetParent(remotePath);
        _stats.TryRemove(remotePath, out _);
        _lists.TryRemove(remotePath, out _);
        _lists.TryRemove(parent, out _);
        _stats.TryRemove(parent, out _);
        _readAheadDirectories.TryRemove(parent, out _);
    }

    /// <summary>Forgets everything cached at or below <paramref name="remotePath"/> (a directory renamed or deleted).</summary>
    private void InvalidateTree(string remotePath)
    {
        Invalidate(remotePath);
        _versions.TryRemove(remotePath, out _);
        string prefix = remotePath + "/";
        RemoveByPrefix(_stats, prefix);
        RemoveByPrefix(_lists, prefix);
        RemoveByPrefix(_versions, prefix);
        RemoveByPrefix(_readAheadDirectories, prefix);
    }

    /// <summary>Records machine B's new metadata of <paramref name="remotePath"/> after a change made through the Broker.</summary>
    public void Update(string remotePath, FsEntry entry)
    {
        Invalidate(remotePath);
        // Machine B's own answer, newer than anything in flight (the epoch moved past those): short TTL, a watch
        // notification of the same change follows and invalidates it anyway.
        _stats[remotePath] = new Timed<FsEntry>(entry, Environment.TickCount64 + (long)_options.MetadataTtl.TotalMilliseconds);
    }

    /// <summary>The cache copy now holds exactly <paramref name="version"/> of machine B (after a write-back).</summary>
    public void MarkContent(string remotePath, FsVersion version) => _versions[remotePath] = version;

    /// <summary>The cache copy no longer matches any version of machine B.</summary>
    public void ForgetContent(string remotePath) => _versions.TryRemove(remotePath, out _);

    /// <summary>
    /// Creates <paramref name="remotePath"/> on machine B (collision if it exists there), then clears the cache name so
    /// the hook's real NtCreateFile creates the cache copy itself, under an existing parent folder.
    /// </summary>
    public async Task<CachedItem> CreateAsync(string remotePath, bool isDirectory, uint attributes, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.CreateAsync(remotePath, isDirectory, attributes, cancellationToken).ConfigureAwait(false);
        string localPath = GetLocalPath(remotePath);
        RemoveLocal(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        _versions.TryRemove(remotePath, out _);
        Update(remotePath, entry);
        return new CachedItem(entry, localPath);
    }

    /// <summary>
    /// An existing remote file is opened to be overwritten: no download (the open truncates it), only a cache file with
    /// machine B's attributes so the real open behaves as on machine B (hidden/system/read-only checks).
    /// </summary>
    public CachedItem PrepareOverwrite(string remotePath, FsEntry entry)
    {
        string localPath = GetLocalPath(remotePath);
        if (!File.Exists(localPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            RemoveLocal(localPath);
            File.WriteAllBytes(localPath, []);
            ApplyMetadata(localPath, entry);
            File.SetAttributes(localPath, Mirrored(entry.Attributes) is var a && a != 0 ? a : FileAttributes.Normal);
            _versions.TryRemove(remotePath, out _);
        }
        return new CachedItem(entry, localPath);
    }

    public async Task<FsEntry> SetInfoAsync(FsSetInfoRequest request, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.SetInfoAsync(request, cancellationToken).ConfigureAwait(false);
        Update(request.Path, entry);
        return entry;
    }

    /// <summary>
    /// Renames on machine B, then prepares the cache for the hook, which renames its open cache handle: the new cache
    /// folder exists and a stale cache file at the new name is gone.
    /// </summary>
    public async Task<CachedItem> RenameAsync(string remotePath, string newRemotePath, bool replaceIfExists, CancellationToken cancellationToken)
    {
        FsEntry entry = await _client.RenameAsync(remotePath, newRemotePath, replaceIfExists, cancellationToken).ConfigureAwait(false);
        bool hadVersion = _versions.TryRemove(remotePath, out FsVersion version);
        InvalidateTree(remotePath);
        InvalidateTree(newRemotePath);
        string newLocalPath = GetLocalPath(newRemotePath);
        Directory.CreateDirectory(Path.GetDirectoryName(newLocalPath)!);
        if (!GetLocalPath(remotePath).Equals(newLocalPath, StringComparison.OrdinalIgnoreCase))
            RemoveLocal(newLocalPath);
        if (hadVersion && !entry.IsDirectory)
            _versions[newRemotePath] = version;
        Update(newRemotePath, entry);
        return new CachedItem(entry, newLocalPath);
    }

    /// <summary>Deletes on machine B, then drops the cache copy.</summary>
    /// <param name="keepItem">The caller deletes the cache file or placeholder itself through its open handle; only
    /// stale cache copies below a directory are removed here.</param>
    /// <param name="expectedFileId">File id the caller saw; another file under the name is left alone (returns false).</param>
    public async Task<bool> DeleteAsync(string remotePath, bool keepItem, ulong? expectedFileId, CancellationToken cancellationToken)
    {
        bool deleted = await _client.DeleteAsync(remotePath, expectedFileId, cancellationToken).ConfigureAwait(false);
        InvalidateTree(remotePath);
        if (!deleted)
            return false;
        string localPath = GetLocalPath(remotePath);
        if (Directory.Exists(localPath))
        {
            // Machine B's directory was empty: leftovers in the placeholder are stale cache copies. The placeholder
            // itself may be held open by the caller, which deletes it.
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
                RemoveLocal(child);
            if (!keepItem)
                TryDeleteDirectory(localPath);
        }
        else if (!keepItem)
        {
            RemoveLocal(localPath);
        }
        return true;
    }

    /// <summary>Removes a stale cache file or folder tree; in-use entries stay.</summary>
    private static void RemoveLocal(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(localPath))
                RemoveLocal(child);
            TryDeleteDirectory(localPath);
            return;
        }
        if (!File.Exists(localPath))
            return;
        try
        {
            File.SetAttributes(localPath, FileAttributes.Normal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        // Rename first: the name is free at once even when a handle still holds the file (a delete would leave it
        // delete-pending under its name until that handle closes, and every open or replace of the name would fail).
        string doomed = $"{localPath}.pbdl-{Guid.NewGuid():N}.tmp";
        try
        {
            File.Move(localPath, doomed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            doomed = localPath;
        }
        TryDelete(doomed);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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
