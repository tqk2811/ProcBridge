using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u32 WatchId | u32 TimeoutMilliseconds</c>. Blocks the pipe instance until the watch has changes, the
/// timeout passes (empty reply) or the watch is removed; <see cref="HookIpcProtocol.InfiniteTimeout"/> waits without
/// limit. The hook sends it on a pipe instance of its own so other requests are not held up.
/// </summary>
public sealed class HookWaitChangesRequest : IHookIpcMessage
{
    public required uint WatchId { get; init; }
    public required uint TimeoutMilliseconds { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WaitChanges;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt32(WatchId);
        writer.WriteUInt32(TimeoutMilliseconds);
    }

    public static HookWaitChangesRequest Read(ref HookIpcReader reader)
    {
        uint id = reader.ReadUInt32();
        return new HookWaitChangesRequest { WatchId = id, TimeoutMilliseconds = reader.ReadUInt32() };
    }
}
