using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body of FetchRange (M8): <c>u32 RangeId | u32 Flags | i64 Offset | i64 Length</c>. <c>RangeId</c> comes from the
/// Resolve reply (<see cref="HookFileInfo.RangeId"/>); <see cref="HookIpcProtocol.FetchFlagWhole"/> asks for the whole file.
/// The reply is <see cref="HookFetchRangeReply"/>.
/// </summary>
public sealed class HookFetchRangeRequest : IHookIpcMessage
{
    public required uint RangeId { get; init; }
    public uint Flags { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }

    public bool Whole => (Flags & HookIpcProtocol.FetchFlagWhole) != 0;

    public HookIpcMessageType MessageType => HookIpcMessageType.FetchRange;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt32(RangeId);
        writer.WriteUInt32(Flags);
        writer.WriteInt64(Offset);
        writer.WriteInt64(Length);
    }

    public static HookFetchRangeRequest Read(ref HookIpcReader reader) => new()
    {
        RangeId = reader.ReadUInt32(),
        Flags = reader.ReadUInt32(),
        Offset = reader.ReadInt64(),
        Length = reader.ReadInt64(),
    };
}
