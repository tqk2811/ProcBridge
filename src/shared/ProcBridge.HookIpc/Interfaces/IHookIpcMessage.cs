using ProcBridge.HookIpc.Enums;

namespace ProcBridge.HookIpc.Interfaces;

/// <summary>A request or reply body that <see cref="HookIpcCodec"/> can encode.</summary>
public interface IHookIpcMessage
{
    HookIpcMessageType MessageType { get; }

    void WriteBody(ref HookIpcWriter writer);
}
