using MessagePack;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol.Models;

/// <summary>One change reported by the Agent's directory watcher.</summary>
[MessagePackObject]
public sealed class FsChange
{
    [Key(0)] public required FsChangeAction Action { get; init; }
    /// <summary>Share-relative path, '/' separated.</summary>
    [Key(1)] public required string Path { get; init; }
}
