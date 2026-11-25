using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Empty body: the watch is gone.</summary>
public sealed class HookUnwatchReply : IHookIpcMessage
{
    public HookIpcMessageType MessageType => HookIpcMessageType.Unwatch;

    public void WriteBody(ref HookIpcWriter writer)
    {
    }
}
