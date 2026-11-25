using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>FileInfo | string NewCachePath</c>. Machine B already renamed; the hook renames its open cache handle to
/// <c>NewCachePath</c> (the Broker made its folder exist and removed a stale cache entry there).
/// </summary>
public sealed class HookRenameReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }
    public required string NewCachePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Rename;

    public void WriteBody(ref HookIpcWriter writer)
    {
        Info.Write(ref writer);
        writer.WriteString(NewCachePath);
    }

    public static HookRenameReply Read(ref HookIpcReader reader)
    {
        HookFileInfo info = HookFileInfo.Read(ref reader);
        return new HookRenameReply { Info = info, NewCachePath = reader.ReadString() };
    }
}
