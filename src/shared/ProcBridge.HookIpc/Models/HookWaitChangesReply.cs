using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;

namespace ProcBridge.HookIpc.Models;

/// <summary>
/// Body: <c>u16 Count | u16 Flags | Count × (u32 Action | string Name)</c>. Flags bit 0
/// (<see cref="HookIpcProtocol.ChangesFlagOverflow"/>) means changes were lost. Count 0 without the flag is a timeout.
/// Changes that do not fit stay queued for the next WaitChanges.
/// </summary>
public sealed class HookWaitChangesReply : IHookIpcMessage
{
    /// <summary>Bytes before the first change.</summary>
    public const int FixedSize = 4;

    public required IReadOnlyList<HookChange> Changes { get; init; }
    public required bool Overflow { get; init; }

    public HookIpcMessageType MessageType => HookIpcMessageType.WaitChanges;

    public void WriteBody(ref HookIpcWriter writer)
    {
        writer.WriteUInt16((ushort)Changes.Count);
        writer.WriteUInt16(Overflow ? HookIpcProtocol.ChangesFlagOverflow : (ushort)0);
        foreach (HookChange change in Changes)
        {
            writer.WriteUInt32(change.Action);
            writer.WriteString(change.Name);
        }
    }

    public static HookWaitChangesReply Read(ref HookIpcReader reader)
    {
        int count = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        var changes = new HookChange[count];
        for (int i = 0; i < count; i++)
        {
            uint action = reader.ReadUInt32();
            changes[i] = new HookChange(action, reader.ReadString());
        }
        return new HookWaitChangesReply { Changes = changes, Overflow = (flags & HookIpcProtocol.ChangesFlagOverflow) != 0 };
    }
}
