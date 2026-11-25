using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>u32 WatchId</c>. The reply has no body; a WaitChanges blocked on the watch returns empty.</summary>
public sealed class HookUnwatchRequest : IHookIpcMessage
{
    public required uint WatchId { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Unwatch;

    public void WriteBody(ref HookIpcWriter writer) => writer.WriteUInt32(WatchId);

    public static HookUnwatchRequest Read(ref HookIpcReader reader) => new() { WatchId = reader.ReadUInt32() };
}
