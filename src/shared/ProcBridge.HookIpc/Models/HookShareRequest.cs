using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u64 WriteToken | u32 TargetPid | u32 Reserved | u64 TargetCreationTime</c>. The calling process holds
/// <c>WriteToken</c> and gave a handle holding it to another process (duplicated into it, or inherited by a child): that
/// process holds the token too, until it closes it or exits. Sent before the caller's own Close. The reply has no body.
/// </summary>
public sealed class HookShareRequest : IHookIpcMessage
{
    public required ulong WriteToken { get; init; }
    public required uint TargetProcessId { get; init; }

    /// <summary>Creation time of the target process (FILETIME, UTC), so a reused process id is not taken for it.</summary>
    public required ulong TargetCreationTime { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Share;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt64(WriteToken);
        writer.WriteUInt32(TargetProcessId);
        writer.WriteUInt32(0);
        writer.WriteUInt64(TargetCreationTime);
    }

    public static HookShareRequest Read(ref HookIpcReader reader)
    {
        ulong token = reader.ReadUInt64();
        uint processId = reader.ReadUInt32();
        reader.ReadUInt32();
        return new HookShareRequest { WriteToken = token, TargetProcessId = processId, TargetCreationTime = reader.ReadUInt64() };
    }
}
