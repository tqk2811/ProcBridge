using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
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

    /// <summary>See <see cref="FileCacheOptions.RangeThreshold"/> (M8); 0 downloads every file whole.</summary>
    public long RangeThreshold { get; init; } = 64L * 1024 * 1024;

    /// <summary>See <see cref="FileCacheOptions.RangeBlockSize"/>.</summary>
    public int RangeBlockSize { get; init; } = 256 * 1024;

    /// <summary>See <see cref="FileCacheOptions.RangePrefetchBlocks"/>.</summary>
    public int RangePrefetchBlocks { get; init; } = 4;

    public TransportOptions Transport { get; init; } = new();

    /// <summary>Identity, paired Agents and the Agent's key: frames are encrypted end to end (plan section 9).</summary>
    public BrokerSecurity? Security { get; init; }

    /// <summary>Reach the Agent through a relay (plan 8.2) instead of <see cref="AgentHost"/>:<see cref="AgentPort"/>.</summary>
    public BrokerRelayOptions? Relay { get; init; }

    /// <summary>Development and old tests only: talk plain frames when <see cref="Security"/> is null. Off by default.</summary>
    public bool AllowPlaintextTransport { get; init; }
    /// <summary>What a write-back does when the file changed on machine B meanwhile (plan section 10).</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.KeepBoth;

    /// <summary>
    /// Write-through: hooks send a flush after every write on a handle that may change content, so machine B gets each
    /// write at once instead of at close or flush (slow; for data that must not wait).
    /// </summary>
    public bool WriteThrough { get; init; }

    /// <summary>
    /// M8: machine B keeps a handle for every open that may write, with that open's share mode, so users of machine B
    /// cannot open the file in a way the open forbids (and an open machine B's users forbid fails). Off: a handle is only
    /// opened for the first byte range lock. Locks are forwarded to machine B either way.
    /// </summary>
    public bool HoldShareMode { get; init; } = true;

    /// <summary>Longest a byte range lock that may wait (not FailImmediately) waits for machine B.</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Folder of pending write-backs; default <c>%LOCALAPPDATA%\ProcBridge\queue</c>. Shared by sessions so a later
    /// Broker for the same Agent and share resends what an earlier one could not.</summary>
    public string? WriteQueueRoot { get; init; }

    /// <summary>How often failed write-backs are tried again while the Broker runs.</summary>
    public TimeSpan WriteRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often a hook request still running (a large download) gets a progress message, so the hook's call timeout
    /// (<c>PROCBRIDGE_IPC_TIMEOUT_MS</c>) counts from the last sign of life instead of from the request; see
    /// <see cref="HookIpcServer.DefaultProgressInterval"/>.
    /// </summary>
    public TimeSpan IpcProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    public string GetWriteQueueRoot() => WriteQueueRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "queue");

    /// <summary>Key of the Agent and share in write queue entries.</summary>
    public string GetWriteTarget() => Relay is { } relay ? $"relay:{(Security is { } security ? KeyFingerprint.Compute(security.AgentPublicKey) : relay.Endpoint.BaseUrl.Host)}/{ShareName}" : $"{AgentHost}:{AgentPort}/{ShareName}";

    public string GetCacheRoot() => CacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "cache", SessionId);
}
