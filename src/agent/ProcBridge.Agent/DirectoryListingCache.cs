namespace ProcBridge.Agent;

/// <summary>
/// Short-lived snapshots of large directory listings, so the pages after the first one of an <c>FsList</c> are cut from
/// the scan made for the first page instead of scanning and sorting the whole directory again for every page (that was
/// O(n²) for a huge directory). A first page (start index 0) always scans afresh and replaces the snapshot, so a new
/// listing never sees stale content; later pages of the same listing see the directory as it was at the first page,
/// which also keeps paging by index stable while the directory changes. Pages may come over any connection of the
/// pool, hence one cache per share rather than per connection.
/// Limits (no client identity in <c>FsList</c>): two clients listing the same directory at once share one snapshot, and a
/// first page of one replaces the snapshot the other is paging through (its next pages then come from the newer scan,
/// as they would without a cache); a pause longer than <see cref="Lifetime"/> between two pages scans again.
/// </summary>
internal sealed class DirectoryListingCache
{
    /// <summary>A snapshot unused for this long is dropped, and a later page scans again.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    /// <summary>Directories kept at once; the oldest snapshot goes first.</summary>
    public const int Capacity = 8;

    private readonly object _lock = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _lifetimeMs;
    private long _scanCount;

    /// <param name="lifetime">Default <see cref="Lifetime"/>; tests shorten it.</param>
    public DirectoryListingCache(TimeSpan? lifetime = null)
    {
        _lifetimeMs = (long)(lifetime ?? Lifetime).TotalMilliseconds;
    }

    /// <summary>Snapshots kept now (tests).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _snapshots.Count;
        }
    }

    /// <summary>Directory scans made so far (tests).</summary>
    public long ScanCount => Interlocked.Read(ref _scanCount);

    /// <summary>The listing of <paramref name="path"/>: a snapshot for a later page when one is fresh, else a new scan.</summary>
    /// <param name="scan">Scans the directory (sorted entries and their details).</param>
    /// <param name="keep">Whether a new scan is worth keeping (it has more than one page).</param>
    public Snapshot Get(string path, bool firstPage, Func<Snapshot> scan, Func<Snapshot, bool> keep)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            // Every call drops what expired, so a large listing nobody pages through does not stay in memory.
            RemoveExpired(now);
            if (!firstPage && _snapshots.TryGetValue(path, out Snapshot? cached))
            {
                cached.LastUsed = now;
                return cached;
            }
        }

        Interlocked.Increment(ref _scanCount);
        Snapshot fresh = scan();
        lock (_lock)
        {
            if (!keep(fresh))
            {
                _snapshots.Remove(path);
                return fresh;
            }
            _snapshots[path] = fresh;
            // The least recently used goes first until the cache fits.
            while (_snapshots.Count > Capacity)
                _snapshots.Remove(_snapshots.MinBy(x => x.Value.LastUsed).Key);
        }
        return fresh;
    }

    private void RemoveExpired(long now)
    {
        foreach (KeyValuePair<string, Snapshot> pair in _snapshots.Where(x => now - x.Value.LastUsed >= _lifetimeMs).ToList())
            _snapshots.Remove(pair.Key);
    }

    /// <summary>One scan of a directory: entries sorted by name and the details read in the same pass.</summary>
    public sealed class Snapshot(FileSystemInfo[] entries, Dictionary<string, FileDetailsReader.FileDetails> details)
    {
        /// <summary>Last scan or page served from it (<see cref="Environment.TickCount64"/>); changed under the cache lock.</summary>
        public long LastUsed { get; set; } = Environment.TickCount64;

        public FileSystemInfo[] Entries { get; } = entries;

        public Dictionary<string, FileDetailsReader.FileDetails> Details { get; } = details;
    }
}
