using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>FileInfo | string CachePath</c>. <c>CachePath</c> is a full DOS path on machine A (file, or the
/// placeholder folder of a directory) whose times and attributes already match machine B; the hook opens it with
/// the real NtCreateFile after prefixing <c>\??\</c>.
/// </summary>
public sealed class HookResolveReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }
    public required string CachePath { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Resolve;

    public void WriteBody(ref HookIpcWriter writer)
    {
        Info.Write(ref writer);
        writer.WriteString(CachePath);
    }

    public static HookResolveReply Read(ref HookIpcReader reader)
    {
        HookFileInfo info = HookFileInfo.Read(ref reader);
        return new HookResolveReply { Info = info, CachePath = reader.ReadString() };
    }
}
