namespace ProcBridge.HookIpc.Models;

/// <summary>One directory entry: <c>FileInfo | string Name</c>.</summary>
public readonly record struct HookListEntry(HookFileInfo Info, string Name)
{
    public int EncodedSize => HookIpcProtocol.FileInfoSize + HookIpcWriter.GetStringSize(Name);
}
