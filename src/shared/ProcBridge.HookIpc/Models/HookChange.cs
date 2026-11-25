namespace ProcBridge.HookIpc.Models;

/// <summary>
/// One change: <c>u32 Action | string Name</c>. <c>Action</c> is a Win32 <c>FILE_ACTION_*</c> value; <c>Name</c> is
/// relative to the watched directory, '\' separated, like <c>FILE_NOTIFY_INFORMATION.FileName</c>.
/// </summary>
public readonly record struct HookChange(uint Action, string Name)
{
    public int EncodedSize => 4 + HookIpcWriter.GetStringSize(Name);
}
