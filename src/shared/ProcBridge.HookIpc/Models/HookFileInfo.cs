namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Fixed 64-byte metadata block, enough to fill FILE_BASIC/STANDARD/NETWORK_OPEN information:
/// <c>u32 Attributes | u32 Reserved | i64 Size | i64 AllocationSize | i64 CreationTime | i64 LastAccessTime |
/// i64 LastWriteTime | i64 ChangeTime | u64 FileId</c>. Times are FILETIME UTC as on machine B.
/// </summary>
public readonly record struct HookFileInfo
{
    public uint Attributes { get; init; }
    public long Size { get; init; }
    public long AllocationSize { get; init; }
    public long CreationTime { get; init; }
    public long LastAccessTime { get; init; }
    public long LastWriteTime { get; init; }
    public long ChangeTime { get; init; }
    public ulong FileId { get; init; }

    public void Write(ref HookIpcWriter writer)
    {
        writer.WriteUInt32(Attributes);
        writer.WriteUInt32(0);
        writer.WriteInt64(Size);
        writer.WriteInt64(AllocationSize);
        writer.WriteInt64(CreationTime);
        writer.WriteInt64(LastAccessTime);
        writer.WriteInt64(LastWriteTime);
        writer.WriteInt64(ChangeTime);
        writer.WriteUInt64(FileId);
    }

    public static HookFileInfo Read(ref HookIpcReader reader)
    {
        uint attributes = reader.ReadUInt32();
        reader.ReadUInt32();
        return new HookFileInfo
        {
            Attributes = attributes,
            Size = reader.ReadInt64(),
            AllocationSize = reader.ReadInt64(),
            CreationTime = reader.ReadInt64(),
            LastAccessTime = reader.ReadInt64(),
            LastWriteTime = reader.ReadInt64(),
            ChangeTime = reader.ReadInt64(),
            FileId = reader.ReadUInt64(),
        };
    }
}
