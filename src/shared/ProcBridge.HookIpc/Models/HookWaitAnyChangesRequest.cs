using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u32 TimeoutMilliseconds</c>. One long poll for every watch of the calling process: blocks the pipe instance
/// until one of them has changes, the process registers a new watch (empty reply, so the hook polls again) or the
/// timeout passes (empty reply); <see cref="HookIpcProtocol.InfiniteTimeout"/> waits without limit. The hook keeps one
/// pipe instance for it, whatever the number of watched handles.
/// </summary>
public sealed class HookWaitAnyChangesRequest : IHookIpcMessage
{
    public required uint TimeoutMilliseconds { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WaitAnyChanges;

    public void WriteBody(ref HookIpcWriter writer) => writer.WriteUInt32(TimeoutMilliseconds);

    public static HookWaitAnyChangesRequest Read(ref HookIpcReader reader) => new() { TimeoutMilliseconds = reader.ReadUInt32() };
}
