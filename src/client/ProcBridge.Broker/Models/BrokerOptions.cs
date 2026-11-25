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

    public TransportOptions Transport { get; init; } = new();

    public string GetCacheRoot() => CacheRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcBridge", "cache", SessionId);
}
