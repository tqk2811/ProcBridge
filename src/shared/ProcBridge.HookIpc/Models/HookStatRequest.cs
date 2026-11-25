using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Reserved | string RelativePath</c>. The path is relative to the virtual root,
/// '\' separated, without leading separator; empty means the root itself.
/// </summary>
public sealed class HookStatRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Stat;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(0);
        writer.WriteString(RelativePath);
    }

    public static HookStatRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        reader.ReadUInt16();
        return new HookStatRequest { MappingIndex = mapping, RelativePath = reader.ReadString() };
    }
}
