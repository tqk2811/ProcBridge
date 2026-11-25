using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>u32 WatchId | u32 Reserved</c>. The id is used by WaitChanges and Unwatch.</summary>
public sealed class HookWatchReply : IHookIpcMessage
{
    public required uint WatchId { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WatchDirectory;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt32(WatchId);
        writer.WriteUInt32(0);
    }

    public static HookWatchReply Read(ref HookIpcReader reader)
    {
        uint id = reader.ReadUInt32();
        reader.ReadUInt32();
        return new HookWatchReply { WatchId = id };
    }
}
