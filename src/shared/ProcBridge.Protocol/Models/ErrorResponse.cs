using MessagePack;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol.Models;

/// <summary>Header of a <see cref="FrameKind.Error"/> frame.</summary>
[MessagePackObject]
public sealed class ErrorResponse
{
    [Key(0)] public required NtStatus Status { get; init; }
    [Key(1)] public string? Message { get; init; }
}
