using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | string RelativePath [| u64 ExpectedFileId]</c>. Flags bit 0: the hook deletes
/// the cache copy itself through its handle (the Broker only clears what is below a directory). Bit 1: ExpectedFileId
/// follows, machine B's file id the handle was opened on (a delete-on-close sent after the close: another file under the
/// name is left alone). The reply has no body.
/// </summary>
public sealed class HookDeleteRequest : IHookIpcMessage
{
    public const ushort FlagKeepItem = 1;
    public const ushort FlagExpectFileId = 2;

    public required ushort MappingIndex { get; init; }
    public ushort Flags { get; init; }
    public required string RelativePath { get; init; }
    public ulong ExpectedFileId { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Delete;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteString(RelativePath);
        if ((Flags & FlagExpectFileId) != 0)
            writer.WriteUInt64(ExpectedFileId);
    }

    public static HookDeleteRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        string path = reader.ReadString();
        ulong fileId = (flags & FlagExpectFileId) != 0 ? reader.ReadUInt64() : 0;
        return new HookDeleteRequest { MappingIndex = mapping, Flags = flags, RelativePath = path, ExpectedFileId = fileId };
    }
}
