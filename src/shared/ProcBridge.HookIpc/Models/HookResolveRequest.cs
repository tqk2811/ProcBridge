using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | u32 DesiredAccess | u32 CreateDisposition | u32 CreateOptions |
/// u32 FileAttributes | u32 ShareAccess | string RelativePath</c>. Access, disposition, options and attributes are the raw
/// NtCreateFile arguments. A read-only share answers STATUS_MEDIA_WRITE_PROTECTED for write access, delete-on-close
/// or a disposition that would create or overwrite; a writable share creates missing names on machine B first (M3).
/// </summary>
public sealed class HookResolveRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public ushort Flags { get; init; }
    public uint DesiredAccess { get; init; }
    public uint CreateDisposition { get; init; }
    public uint CreateOptions { get; init; }
    public uint FileAttributes { get; init; }
    /// <summary>NtCreateFile ShareAccess (FILE_SHARE_*), used for the handle machine B keeps for a writer (M8).</summary>
    public uint ShareAccess { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Resolve;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(Flags);
        writer.WriteUInt32(DesiredAccess);
        writer.WriteUInt32(CreateDisposition);
        writer.WriteUInt32(CreateOptions);
        writer.WriteUInt32(FileAttributes);
        writer.WriteUInt32(ShareAccess);
        writer.WriteString(RelativePath);
    }

    public static HookResolveRequest Read(ref HookIpcReader reader) => new()
    {
        MappingIndex = reader.ReadUInt16(),
        Flags = reader.ReadUInt16(),
        DesiredAccess = reader.ReadUInt32(),
        CreateDisposition = reader.ReadUInt32(),
        CreateOptions = reader.ReadUInt32(),
        FileAttributes = reader.ReadUInt32(),
        ShareAccess = reader.ReadUInt32(),
        RelativePath = reader.ReadString(),
    };
}
