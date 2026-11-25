using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>Body: <c>u32 ProcessId | u16 Architecture | u16 Reserved</c>.</summary>
public sealed class HookHelloRequest : IHookIpcMessage
{
    public required uint ProcessId { get; init; }
    public required HookArchitecture Architecture { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Hello;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt32(ProcessId);
        writer.WriteUInt16((ushort)Architecture);
        writer.WriteUInt16(0);
    }

    public static HookHelloRequest Read(ref HookIpcReader reader)
    {
        uint processId = reader.ReadUInt32();
        var architecture = (HookArchitecture)reader.ReadUInt16();
        reader.ReadUInt16();
        return new HookHelloRequest { ProcessId = processId, Architecture = architecture };
    }
}
