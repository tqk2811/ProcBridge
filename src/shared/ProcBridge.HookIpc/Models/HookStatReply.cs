using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>FileInfo</c>. Also the reply of SetInfo (machine B's metadata after the change).</summary>
public sealed class HookStatReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }

    /// <summary>Stat, or SetInfo for the reply of a SetInfo request.</summary>
    public HookIpcMessageType Type { get; init; } = HookIpcMessageType.Stat;

    public HookIpcMessageType MessageType => Type;

    public void WriteBody(ref HookIpcWriter writer) => Info.Write(ref writer);

    public static HookStatReply Read(ref HookIpcReader reader) => new() { Info = HookFileInfo.Read(ref reader) };
}
