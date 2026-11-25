using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingIndex | u16 Flags | u32 CompletionFilter | string RelativePath</c>. Sent when the process
/// first calls NtNotifyChangeDirectoryFile[Ex] on a handle of a virtual directory. Flags bit 0
/// (<see cref="HookIpcProtocol.WatchFlagSubtree"/>) is <c>WatchTree</c>; <c>CompletionFilter</c> is the
/// <c>FILE_NOTIFY_CHANGE_*</c> mask as received (the Broker cannot apply it exactly: machine B does not say which
/// attribute changed, so the hook may see a change the filter would hide on a local volume).
/// </summary>
public sealed class HookWatchRequest : IHookIpcMessage
{
    public required ushort MappingIndex { get; init; }
    public required bool WatchSubtree { get; init; }
    public uint CompletionFilter { get; init; }
    public required string RelativePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WatchDirectory;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16(MappingIndex);
        writer.WriteUInt16(WatchSubtree ? HookIpcProtocol.WatchFlagSubtree : (ushort)0);
        writer.WriteUInt32(CompletionFilter);
        writer.WriteString(RelativePath);
    }

    public static HookWatchRequest Read(ref HookIpcReader reader)
    {
        ushort mapping = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        uint filter = reader.ReadUInt32();
        return new HookWatchRequest
        {
            MappingIndex = mapping,
            WatchSubtree = (flags & HookIpcProtocol.WatchFlagSubtree) != 0,
            CompletionFilter = filter,
            RelativePath = reader.ReadString(),
        };
    }
}
