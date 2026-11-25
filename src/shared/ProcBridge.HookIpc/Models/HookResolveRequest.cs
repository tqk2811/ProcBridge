using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | u32 DesiredAccess | u32 CreateDisposition | u32 CreateOptions |
/// string RelativePath</c>. Access, disposition and options are the raw NtCreateFile arguments. While shares are
/// read only (M2) the Broker answers STATUS_MEDIA_WRITE_PROTECTED for write access, delete-on-close or a
/// disposition that would create or overwrite.
/// </summary>
public sealed class HookResolveRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public ushort Flags { get; init; }
    public uint DesiredAccess { get; init; }
    public uint CreateDisposition { get; init; }
    public uint CreateOptions { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Resolve;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteUInt32(DesiredAccess);
        writer.WriteUInt32(CreateDisposition);
        writer.WriteUInt32(CreateOptions);
        writer.WriteString(RelativePath);
    }

    public static HookResolveRequest Read(ref HookIpcReader reader) => new()
    {
        MappingIndex = reader.ReadUInt16(),
        Flags = reader.ReadUInt16(),
        DesiredAccess = reader.ReadUInt32(),
        CreateDisposition = reader.ReadUInt32(),
        CreateOptions = reader.ReadUInt32(),
        RelativePath = reader.ReadString(),
    };
}
