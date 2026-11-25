using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body of Close and Flush: <c>u64 WriteToken</c> from the Resolve reply. The reply has no body.</summary>
public sealed class HookTokenRequest : IHookIpcMessage
{
    public required HookIpcMessageType Type { get; init; }
    public required ulong WriteToken { get; init; }

    public HookIpcMessageType MessageType => Type;

    public void WriteBody(ref HookIpcWriter writer) => writer.WriteUInt64(WriteToken);

    public static HookTokenRequest Read(ref HookIpcReader reader, HookIpcMessageType type) => new() { Type = type, WriteToken = reader.ReadUInt64() };
}
