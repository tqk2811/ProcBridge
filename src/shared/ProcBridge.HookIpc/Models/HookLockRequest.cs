using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body of Lock and Unlock (M8): <c>u64 WriteToken | i64 Offset | i64 Length | u32 Key | u16 Flags | u16 Reserved</c>.
/// Flags (Lock only): <see cref="HookIpcProtocol.LockFlagExclusive"/>, <see cref="HookIpcProtocol.LockFlagFailImmediately"/>.
/// The reply has no body; a lock held by someone else on machine B fails with STATUS_LOCK_NOT_GRANTED.
/// </summary>
public sealed class HookLockRequest : IHookIpcMessage
{
    public required HookIpcMessageType Type { get; init; }
    public required ulong WriteToken { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public uint Key { get; init; }
    public ushort Flags { get; init; }

    public bool Exclusive => (Flags & HookIpcProtocol.LockFlagExclusive) != 0;

    public bool FailImmediately => (Flags & HookIpcProtocol.LockFlagFailImmediately) != 0;

    public HookIpcMessageType MessageType => Type;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt64(WriteToken);
        writer.WriteInt64(Offset);
        writer.WriteInt64(Length);
        writer.WriteUInt32(Key);
        writer.WriteUInt16(Flags);
        writer.WriteUInt16(0);
    }

    public static HookLockRequest Read(ref HookIpcReader reader, HookIpcMessageType type)
    {
        var request = new HookLockRequest
        {
            Type = type,
            WriteToken = reader.ReadUInt64(),
            Offset = reader.ReadInt64(),
            Length = reader.ReadInt64(),
            Key = reader.ReadUInt32(),
            Flags = reader.ReadUInt16(),
        };
        reader.ReadUInt16();
        return request;
    }
}
