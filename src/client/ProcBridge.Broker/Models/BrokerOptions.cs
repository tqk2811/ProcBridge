using ProcBridge.Protocol;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker.Models;

public sealed record BrokerOptions
{
    /// <summary>Session id; names the pipe (<c>ProcBridge-&lt;id&gt;</c>) and the cache folder.</summary>
    public required string SessionId { get; init; }

    public required string AgentHost { get; init; }

    public int AgentPort { get; init; } = ProtocolInfo.DefaultPort;

    public required string ShareName { get; init; }

    public required IReadOnlyList<VirtualMapping> Mappings { get; init; }

    /// <summary>Cache folder; default <c>%LOCALAPPDATA%\ProcBridge\cache\&lt;session&gt;</c> (plan 4.4).</summary>
    public string? CacheRoot { get; init; }

    /// <summary>How long stat and list results are reused before asking machine B again (plan section 10).</summary>
    public TimeSpan MetadataTtl { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Subscribe to change notifications of the Agent (M7) and invalidate the cache by them instead of by TTL.</summary>
    public bool WatchChanges { get; init; } = true;

    /// <summary>Safety lifetime of metadata while the change watcher is active.</summary>
    public TimeSpan WatchedMetadataTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before the change watcher reconnects after losing its connection.</summary>
    public TimeSpan WatchReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Load the metadata of every mapped folder with one tree walk when the session starts (plan section 11).</summary>
    public bool PreloadTree { get; init; } = true;

    /// <summary>Depth of the start-up tree walk; 0 lets the Agent choose.</summary>
    public int PreloadTreeDepth { get; init; }

    /// <summary>Read-ahead of small files, see <see cref="FileCacheOptions.ReadAheadMaxFileSize"/>; 0 turns it off.</summary>
    public long ReadAheadMaxFileSize { get; init; } = 64 * 1024;

    /// <summary>See <see cref="FileCacheOptions.ReadAheadMaxFiles"/>.</summary>
    public int ReadAheadMaxFiles { get; init; } = 64;

    /// <summary>See <see cref="FileCacheOptions.ReadAheadParallelism"/>.</summary>
    public int ReadAheadParallelism { get; init; } = 8;

    public TransportOptions Transport { get; init; } = new();

    public string GetCacheRoot() => CacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "cache", SessionId);
}
