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

    /// <summary>
    /// Read-ahead downloads running at once across all directories. They share the connection pool with foreground
    /// requests (streams multiplexed on the same connections), so this mostly bounds the bytes queued ahead of them on
    /// a slow link.
    /// </summary>
    public int ReadAheadParallelism { get; init; } = 8;

    /// <summary>
    /// Files larger than this are cached by ranges (M8): a sparse copy of the right size whose blocks are downloaded
    /// when a read needs them. Opens that may write, and memory mappings, bring the whole file in first. 0 turns it off.
    /// </summary>
    public long RangeThreshold { get; init; } = 64L * 1024 * 1024;

    /// <summary>Block size of ranged files: the least a read downloads.</summary>
    public int RangeBlockSize { get; init; } = 256 * 1024;

    /// <summary>Blocks prefetched in the background when reads of a ranged file are sequential.</summary>
    public int RangePrefetchBlocks { get; init; } = 4;

    /// <summary>Blocks downloaded at once when a ranged file is brought in whole.</summary>
    public int RangeParallelism { get; init; } = 4;
}
