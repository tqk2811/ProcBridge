using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>u16 MappingIndex | u16 Reserved | u32 StartIndex | string RelativePath</c>.</summary>
public sealed class HookListRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public uint StartIndex { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.List;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(0);
        writer.WriteUInt32(StartIndex);
        writer.WriteString(RelativePath);
    }

    public static HookListRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        reader.ReadUInt16();
        uint start = reader.ReadUInt32();
        return new HookListRequest { MappingIndex = mapping, StartIndex = start, RelativePath = reader.ReadString() };
    }
}
