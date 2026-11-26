using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | u32 GrantedAccess | string RelativePath</c>. The hook found a handle it did not
/// open whose real name is the cache copy of a virtual path (duplicated into this process, inherited): it asks machine B's
/// metadata and, when the handle may write (<see cref="HookIpcProtocol.AdoptFlagWrite"/>), the write token this process
/// holds for it. Reply: <see cref="HookAdoptReply"/>.
/// </summary>
public sealed class HookAdoptRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public required string RelativePath { get; init; }

    /// <summary><see cref="HookIpcProtocol.AdoptFlagWrite"/>: the handle can change the file's content.</summary>
    public ushort Flags { get; init; }

    /// <summary>Access of the handle (ObjectBasicInformation), for the log.</summary>
    public uint GrantedAccess { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Adopt;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteUInt32(GrantedAccess);
        writer.WriteString(RelativePath);
    }

    public static HookAdoptRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        uint access = reader.ReadUInt32();
        return new HookAdoptRequest { MappingIndex = mapping, Flags = flags, GrantedAccess = access, RelativePath = reader.ReadString() };
    }
}
