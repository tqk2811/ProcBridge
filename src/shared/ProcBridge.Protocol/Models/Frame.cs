using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// One unit on the wire. <see cref="Header"/> is MessagePack; <see cref="Payload"/> is raw bytes (file data)
/// that the serializer never wraps, so file data is not copied by MessagePack.
/// </summary>
/// <remarks>
/// Body layout (little endian, before the optional frame protector):
/// <c>u8 kind | u16 messageType | u32 requestId | u32 headerLength | header | payload</c>.
/// The stream carries <c>u32 bodyLength</c> in front of each (protected) body.
/// </remarks>
public readonly struct Frame
{
    /// <summary>Size of the fixed part of the body before the header bytes.</summary>
    public const int FixedBodySize = 1 + 2 + 4 + 4;

    public required FrameKind Kind { get; init; }
    public required MessageType MessageType { get; init; }
    /// <summary>Multiplexing id chosen by the requester; the response echoes it.</summary>
    public required uint RequestId { get; init; }
    public ReadOnlyMemory<byte> Header { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }

    public int BodyLength => FixedBodySize + Header.Length + Payload.Length;
}
