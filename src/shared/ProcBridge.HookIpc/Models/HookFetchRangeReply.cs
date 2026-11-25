using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Reply of FetchRange: <c>u32 Flags</c> (<see cref="HookIpcProtocol.FetchReplyFlagComplete"/>).</summary>
public sealed class HookFetchRangeReply : IHookIpcMessage
{
    public uint Flags { get; init; }

    public bool Complete => (Flags & HookIpcProtocol.FetchReplyFlagComplete) != 0;

    public HookIpcMessageType MessageType => HookIpcMessageType.FetchRange;

    public void WriteBody(ref HookIpcWriter writer) => writer.WriteUInt32(Flags);

    public static HookFetchRangeReply Read(ref HookIpcReader reader) => new() { Flags = reader.ReadUInt32() };
}
