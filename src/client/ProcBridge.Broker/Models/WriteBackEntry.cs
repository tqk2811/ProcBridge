using ProcBridge.Protocol.Models;

namespace ProcBridge.Broker.Models;

/// <summary>
/// One pending write-back, saved as <c>&lt;Id&gt;.json</c> next to a copy of the content (<c>&lt;Id&gt;.data</c>) in the
/// write queue folder before the upload starts, so a Broker that dies mid-way sends it again at the next start
/// (plan section 10 and 15).
/// </summary>
public sealed record WriteBackEntry
{
    public required string Id { get; init; }

    /// <summary>Agent and share the entry belongs to (<c>host:port/share</c>); a Broker only resends its own.</summary>
    public required string Target { get; init; }

    public required string RemotePath { get; init; }

    /// <summary>Version of machine B the content was derived from; null skips the conflict check.</summary>
    public FsVersion? ExpectedVersion { get; init; }

    /// <summary>UTC time the entry was queued, for logs.</summary>
    public DateTime QueuedAt { get; init; }
}
