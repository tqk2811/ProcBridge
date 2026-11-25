using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>FileInfo</c>.</summary>
public sealed class HookStatReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Stat;

    public void WriteBody(ref HookIpcWriter writer) => Info.Write(ref writer);

    public static HookStatReply Read(ref HookIpcReader reader) => new() { Info = HookFileInfo.Read(ref reader) };
}
