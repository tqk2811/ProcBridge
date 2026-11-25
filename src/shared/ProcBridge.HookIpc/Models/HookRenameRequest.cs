using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 NewMappingIndex | u16 Flags | u16 Reserved | string RelativePath |
/// string NewRelativePath</c>. Flags bit 0: replace an existing file. Reply: <see cref="HookRenameReply"/>.
/// </summary>
public sealed class HookRenameRequest : IHookIpcMessage
{
    public const ushort FlagReplaceIfExists = 1;

    public required ushort MappingIndex { get; init; }
    public required ushort NewMappingIndex { get; init; }
    public ushort Flags { get; init; }
    public required string RelativePath { get; init; }
    public required string NewRelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Rename;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(NewMappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteUInt16(0);
        writer.WriteString(RelativePath);
        writer.WriteString(NewRelativePath);
    }

    public static HookRenameRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        ushort newMapping = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        reader.ReadUInt16();
        string path = reader.ReadString();
        return new HookRenameRequest { MappingIndex = mapping, NewMappingIndex = newMapping, Flags = flags, RelativePath = path, NewRelativePath = reader.ReadString() };
    }
}
