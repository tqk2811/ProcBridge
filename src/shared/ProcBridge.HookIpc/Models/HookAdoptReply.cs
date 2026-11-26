using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>FileInfo | u64 WriteToken</c>. Machine B's metadata of the adopted file (<c>RangeId</c> set when its cache
/// copy is still downloaded by ranges) and the write token the process now holds for it, 0 for none (read-only handle).
/// </summary>
public sealed class HookAdoptReply : IHookIpcMessage
{
    public required HookFileInfo Info { get; init; }

    public ulong WriteToken { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Adopt;

    public void WriteBody(ref HookIpcWriter writer)
    {
        Info.Write(ref writer);
        writer.WriteUInt64(WriteToken);
    }

    public static HookAdoptReply Read(ref HookIpcReader reader)
    {
        HookFileInfo info = HookFileInfo.Read(ref reader);
        return new HookAdoptReply { Info = info, WriteToken = reader.ReadUInt64() };
    }
}
