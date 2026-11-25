using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>FileInfo | u64 WriteToken | string CachePath</c>. <c>CachePath</c> is a full DOS path on machine A (file, or the
/// placeholder folder of a directory) whose times and attributes already match machine B; the hook opens it with
/// the real NtCreateFile after prefixing <c>\??\</c>.
/// </summary>
public sealed class HookResolveReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }
    public required string CachePath { get; init; }

    /// <summary>Non-zero when the open may change the content: the hook sends it back in Close/Flush (M3).</summary>
    public ulong WriteToken { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Resolve;

    public void WriteBody(ref HookIpcWriter writer)
    {
        Info.Write(ref writer);
        writer.WriteUInt64(WriteToken);
        writer.WriteString(CachePath);
    }

    public static HookResolveReply Read(ref HookIpcReader reader)
    {
        HookFileInfo info = HookFileInfo.Read(ref reader);
        ulong token = reader.ReadUInt64();
        return new HookResolveReply { Info = info, WriteToken = token, CachePath = reader.ReadString() };
    }
}
