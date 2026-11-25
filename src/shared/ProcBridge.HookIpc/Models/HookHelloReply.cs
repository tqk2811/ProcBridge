using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 MappingCount | u16 Reserved | MappingCount × string VirtualRoot</c>. Virtual roots are DOS paths on
/// machine A without trailing separator (<c>D:\Remote\MyProject</c>); the index in this list is the
/// <c>MappingIndex</c> used by later requests.
/// </summary>
public sealed class HookHelloReply : IHookIpcMessage
{
    public required IReadOnlyList<string> VirtualRoots { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.Hello;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16((ushort)VirtualRoots.Count);
        writer.WriteUInt16(0);
        foreach (string root in VirtualRoots)
            writer.WriteString(root);
    }

    public static HookHelloReply Read(ref HookIpcReader reader)
    {
        int count = reader.ReadUInt16();
        reader.ReadUInt16();
        var roots = new string[count];
        for (int i = 0; i < count; i++)
            roots[i] = reader.ReadString();
        return new HookHelloReply { VirtualRoots = roots };
    }
}
