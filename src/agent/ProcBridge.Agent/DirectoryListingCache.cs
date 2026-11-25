namespace ProcBridge.Agent;

/// <summary>
/// Short-lived snapshots of large directory listings, so the pages after the first one of an <c>FsList</c> are cut from
/// the scan made for the first page instead of scanning and sorting the whole directory again for every page (that was
/// O(n²) for a huge directory). A first page (start index 0) always scans afresh and replaces the snapshot, so a new
/// listing never sees stale content; later pages of the same listing see the directory as it was at the first page,
/// which also keeps paging by index stable while the directory changes. Pages may come over any connection of the
/// pool, hence one cache per share rather than per connection.
/// </summary>
internal sealed class DirectoryListingCache
{
    /// <summary>A snapshot older than this is scanned again even for a later page.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    /// <summary>Directories kept at once; the oldest snapshot goes first.</summary>
    public const int Capacity = 8;

    private readonly object _lock = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private long _scanCount;

    /// <summary>Directory scans made so far (tests).</summary>
    public long ScanCount => Interlocked.Read(ref _scanCount);

    /// <summary>The listing of <paramref name="path"/>: a snapshot for a later page when one is fresh, else a new scan.</summary>
    /// <param name="scan">Scans the directory (sorted entries and their details).</param>
    /// <param name="keep">Whether a new scan is worth keeping (it has more than one page).</param>
    public Snapshot Get(string path, bool firstPage, Func<Snapshot> scan, Func<Snapshot, bool> keep)
    {
        long now = Environment.TickCount64;
        if (!firstPage)
        {
            lock (_lock)
            {
                if (_snapshots.TryGetValue(path, out Snapshot? cached) && now - cached.CreatedAt < Lifetime.TotalMilliseconds)
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
            // Expired snapshots first, then the oldest, until the cache fits.
            foreach (KeyValuePair<string, Snapshot> pair in _snapshots.Where(x => now - x.Value.CreatedAt >= Lifetime.TotalMilliseconds).ToList())
                _snapshots.Remove(pair.Key);
            while (_snapshots.Count > Capacity)
                _snapshots.Remove(_snapshots.MinBy(x => x.Value.CreatedAt).Key);
        }
        return fresh;
    }

    /// <summary>One scan of a directory: entries sorted by name and the details read in the same pass.</summary>
    public sealed class Snapshot(FileSystemInfo[] entries, Dictionary<string, FileDetailsReader.FileDetails> details)
    {
        public long CreatedAt { get; } = Environment.TickCount64;

        public FileSystemInfo[] Entries { get; } = entries;

        public Dictionary<string, FileDetailsReader.FileDetails> Details { get; } = details;
    }
}
