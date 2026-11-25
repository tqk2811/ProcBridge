using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | string RelativePath</c>. Flags bit 0: the hook deletes the cache copy itself
/// through its handle (the Broker only clears what is below a directory). The reply has no body.
/// </summary>
public sealed class HookDeleteRequest : IHookIpcMessage
{
    public const ushort FlagKeepItem = 1;

    public required ushort MappingIndex { get; init; }
    public ushort Flags { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Delete;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteString(RelativePath);
    }

    public static HookDeleteRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        return new HookDeleteRequest { MappingIndex = mapping, Flags = flags, RelativePath = reader.ReadString() };
    }
}
