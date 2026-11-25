namespace ProcBridge.Broker.Models;

/// <summary>Tuning of <see cref="FileCache"/>.</summary>
public sealed record FileCacheOptions
{
    /// <summary>How long stat and list results are reused when no change watcher is active (plan section 10).</summary>
    public TimeSpan MetadataTtl { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Lifetime of stat and list results while the change watcher is active: notifications invalidate them, this is
    /// only a safety net against a lost notification.
    /// </summary>
    public TimeSpan WatchedMetadataTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Read-ahead (plan section 11): when a file this small or smaller is opened, the other files of its directory up to
    /// this size are downloaded in the background. 0 turns read-ahead off.
    /// </summary>
    public long ReadAheadMaxFileSize { get; init; } = 64 * 1024;

    /// <summary>Most files one directory read-ahead downloads.</summary>
    public int ReadAheadMaxFiles { get; init; } = 64;

    /// <summary>Read-ahead downloads running at once (they share the connection pool with foreground requests).</summary>
    public int ReadAheadParallelism { get; init; } = 8;
}
