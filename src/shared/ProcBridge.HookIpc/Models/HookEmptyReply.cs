using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Successful reply without body (Close, Flush, Delete).</summary>
public sealed class HookEmptyReply : IHookIpcMessage
{
    public required HookIpcMessageType Type { get; init; }

    public HookIpcMessageType MessageType => Type;

    public void WriteBody(ref HookIpcWriter writer)
    {
    }
}
