using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>u16 MappingIndex | u16 Reserved | string RelativePath</c>. The reply has no body.</summary>
public sealed class HookDeleteRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Delete;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(0);
        writer.WriteString(RelativePath);
    }

    public static HookDeleteRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        reader.ReadUInt16();
        return new HookDeleteRequest { MappingIndex = mapping, RelativePath = reader.ReadString() };
    }
}
