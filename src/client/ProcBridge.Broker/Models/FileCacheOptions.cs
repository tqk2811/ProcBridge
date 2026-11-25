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
}
