using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Fields | u32 Attributes | i64 CreationTime | i64 LastAccessTime | i64 LastWriteTime |
/// i64 EndOfFile | string RelativePath</c>. Only the fields named in <see cref="Fields"/> are applied. Reply: FileInfo.
/// </summary>
public sealed class HookSetInfoRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public required HookSetInfoFields Fields { get; init; }
    public uint Attributes { get; init; }
    public long CreationTime { get; init; }
    public long LastAccessTime { get; init; }
    public long LastWriteTime { get; init; }
    public long EndOfFile { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.SetInfo;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16((ushort)Fields);
        writer.WriteUInt32(Attributes);
        writer.WriteInt64(CreationTime);
        writer.WriteInt64(LastAccessTime);
        writer.WriteInt64(LastWriteTime);
        writer.WriteInt64(EndOfFile);
        writer.WriteString(RelativePath);
    }

    public static HookSetInfoRequest Read(ref HookIpcReader reader) => new()
    {
        MappingIndex = reader.ReadUInt16(),
        Fields = (HookSetInfoFields)reader.ReadUInt16(),
        Attributes = reader.ReadUInt32(),
        CreationTime = reader.ReadInt64(),
        LastAccessTime = reader.ReadInt64(),
        LastWriteTime = reader.ReadInt64(),
        EndOfFile = reader.ReadInt64(),
        RelativePath = reader.ReadString(),
    };
}
