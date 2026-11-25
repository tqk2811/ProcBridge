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

    public TransportOptions Transport { get; init; } = new();

    /// <summary>What a write-back does when the file changed on machine B meanwhile (plan section 10).</summary>
    public ConflictPolicy ConflictPolicy { get; init; } = ConflictPolicy.KeepBoth;

    /// <summary>
    /// Write-through: hooks send a flush after every write on a handle that may change content, so machine B gets each
    /// write at once instead of at close or flush (slow; for data that must not wait).
    /// </summary>
    public bool WriteThrough { get; init; }

    /// <summary>Folder of pending write-backs; default <c>%LOCALAPPDATA%\ProcBridge\queue</c>. Shared by sessions so a later
    /// Broker for the same Agent and share resends what an earlier one could not.</summary>
    public string? WriteQueueRoot { get; init; }

    /// <summary>How often failed write-backs are tried again while the Broker runs.</summary>
    public TimeSpan WriteRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    public string GetWriteQueueRoot() => WriteQueueRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "queue");

    /// <summary>Key of the Agent and share in write queue entries.</summary>
    public string GetWriteTarget() => $"{AgentHost}:{AgentPort}/{ShareName}";

    public string GetCacheRoot() => CacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "cache", SessionId);
}
